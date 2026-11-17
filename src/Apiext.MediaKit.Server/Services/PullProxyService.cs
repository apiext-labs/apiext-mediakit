using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Apiext.MediaKit.Server.Apis;
using Apiext.MediaKit.Server.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Apiext.MediaKit.Server.Services;

/// <summary>拉流代理展示行（DB 记录 + ZLM 在线状态合并）</summary>
public class ProxyRow
{
    public string Vhost { get; set; } = "__defaultVhost__";
    public string App { get; set; } = string.Empty;
    public string Stream { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public int? AudioType { get; set; }
    public string PullMode { get; set; } = "native";
    public string OutputCodec { get; set; } = "copy";
    public string FfmpegKey { get; set; } = string.Empty;
    public bool EnableOnDemand { get; set; }
    public bool EnableRecord { get; set; }
    public bool IsRecording { get; set; }
    public bool IsOnline { get; set; }
    public int TotalReaderCount { get; set; }
    public int RePullCount { get; set; }
    public long BytesSpeed { get; set; }
    public long AliveSecond { get; set; }
    public string StatusText { get; set; } = string.Empty;
    public string SpeedText { get; set; } = string.Empty;
}

public class PullProxyService
{
    private readonly AppDbContext _db;
    private readonly ZlmService _zlm;
    private readonly ZlmOnlineCache _online;
    private readonly ILogger<PullProxyService> _logger;

    public PullProxyService(AppDbContext db, ZlmService zlm, ZlmOnlineCache online, ILogger<PullProxyService> logger)
    {
        _db = db;
        _zlm = zlm;
        _online = online;
        _logger = logger;
    }

    /// <summary>拉流代理列表（含在线状态、观众数、码率）</summary>
    public async Task<List<ProxyRow>> ListAsync()
    {
        var rows = await _db.PullProxies
            .OrderBy(p => p.CreatedAt)
            .Select(p => new ProxyRow
            {
                Vhost = p.Vhost,
                App = p.App,
                Stream = p.Stream,
                Url = p.Url,
                AudioType = p.AudioType,
                PullMode = p.PullMode,
                OutputCodec = p.OutputCodec,
                FfmpegKey = p.FfmpegKey,
                EnableOnDemand = p.EnableOnDemand,
                EnableRecord = p.EnableRecord,
            })
            .ToListAsync();

        var proxyMap = await LoadProxyMapAsync();
        var mediaMap = await LoadMediaMapAsync();

        foreach (var row in rows)
        {
            var key = $"{row.Vhost}/{row.App}/{row.Stream}";

            if (mediaMap.TryGetValue(key, out var media))
            {
                row.IsOnline = true;
                row.BytesSpeed = media.BytesSpeed;
                row.AliveSecond = media.AliveSecond;
                row.TotalReaderCount = media.TotalReaderCount;
            }
            else if (_online.TryGet(row.Vhost, row.App, row.Stream, out var cached) &&
                     DateTime.UtcNow - cached.RegisteredAtUtc < TimeSpan.FromMinutes(10))
            {
                // getMediaList 未命中时用 on_stream_changed 缓存补位（流刚注册、ZLM 抖动）；
                // 超过 10 分钟的缓存视为过期，避免注销事件丢失导致长期误报在线
                row.IsOnline = true;
            }

            if (proxyMap.TryGetValue(key, out var proxy))
            {
                row.RePullCount = proxy.RePullCount;
                if (proxy.TotalReaderCount > 0)
                {
                    row.TotalReaderCount = proxy.TotalReaderCount;
                }
            }

            if (row.EnableRecord)
            {
                try
                {
                    var rec = await _zlm.IsRecordingAsync(row.App, row.Stream);
                    row.IsRecording = rec.Code == 0 && rec.Status;
                }
                catch
                {
                    // 忽略
                }
            }

            row.StatusText = row.IsOnline ? "在线" : "离线";
            row.SpeedText = FormatSpeed(row.BytesSpeed);
        }

        return rows;
    }

    /// <summary>
    /// 测试源流地址连通性（不落库、不在 ZLM 建拉流任务）：TCP 建连，RTSP 额外发 OPTIONS 探测。
    /// 只验证「地址可达 / 是否需要鉴权」，不代表端到端一定能出画面（编码、鉴权方式只有真正拉流时才知道）。
    /// </summary>
    public async Task<(bool Ok, string Message)> TestSourceAsync(string url)
    {
        if (!IsValidUrl(url))
            return (false, "源流地址必须以 rtsp://、rtmp://、http:// 或 https:// 开头");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            return (false, "源流地址格式不正确，无法解析主机名");

        var port = uri.Port;
        if (port <= 0)
        {
            port = uri.Scheme.ToLowerInvariant() switch
            {
                "rtsp" => 554,
                "rtmp" => 1935,
                "https" => 443,
                _ => 80,
            };
        }

        var target = $"{uri.Host}:{port}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(uri.Host, port, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return (false, $"连接 {target} 超时（6 秒），请检查 IP / 端口 / 防火墙");
        }
        catch (Exception ex)
        {
            return (false, $"连接 {target} 失败：{ex.Message}");
        }

        if (!uri.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase))
            return (true, $"连接 {target} 成功（能否取流取决于账号密码与编码）");

        try
        {
            using var stream = tcp.GetStream();
            var request = $"OPTIONS {url} RTSP/1.0\r\nCSeq: 1\r\nUser-Agent: ApiextMediaKit\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cts.Token);

            var buffer = new byte[512];
            var read = await stream.ReadAsync(buffer, cts.Token);
            var statusLine = read > 0
                ? Encoding.ASCII.GetString(buffer, 0, read).Split('\r', '\n')[0]
                : string.Empty;

            if (statusLine.Contains(" 200", StringComparison.Ordinal))
                return (true, $"RTSP {target} 可达，OPTIONS 返回 200");
            if (statusLine.Contains(" 401", StringComparison.Ordinal) || statusLine.Contains(" 403", StringComparison.Ordinal))
                return (true, $"RTSP {target} 可达，但需要鉴权（{statusLine}）；请确认用户名/密码/通道号");
            if (string.IsNullOrWhiteSpace(statusLine))
                return (true, $"RTSP 端口 {target} 可达，但未收到响应（部分设备限制 OPTIONS）");
            return (true, $"RTSP {target} 可达，返回：{statusLine}");
        }
        catch (Exception ex)
        {
            return (true, $"RTSP 端口 {target} 可达，OPTIONS 探测未完成：{ex.Message}");
        }
    }

    /// <param name="force">
    /// 强制添加：ZLM 启动拉流失败时仍保留配置（返回成功 + 告警），供现场先占位、稍后重新拉流；
    /// 为 false 时启动失败会回滚刚写入的记录，避免「提示失败但记录已存在」。
    /// </param>
    public async Task<(bool Ok, string? Error)> AddAsync(
        string vhost, string app, string stream, string url, int? audioType,
        bool enableOnDemand = false, bool enableRecord = false, string pullMode = "native", string outputCodec = "copy",
        bool force = false)
    {
        if (!IsValidName(app) || !IsValidName(stream))
            return (false, "应用名/流 ID 只能包含字母、数字、下划线(_)、短横线(-)或英文句点(.)");
        if (!IsValidUrl(url))
            return (false, "源流地址必须以 rtsp://、rtmp://、http:// 或 https:// 开头");

        var exists = await _db.PullProxies.AnyAsync(p =>
            p.Vhost == vhost && p.App == app && p.Stream == stream);
        if (exists)
            return (false, "该流已存在");

        var entity = new PullProxy
        {
            Vhost = vhost,
            App = app,
            Stream = stream,
            Url = url,
            AudioType = audioType,
            PullMode = pullMode is "ffmpeg" ? "ffmpeg" : "native",
            OutputCodec = outputCodec is "h264" or "h265" ? outputCodec : "copy",
            EnableOnDemand = enableOnDemand,
            EnableRecord = enableRecord,
            CreatedAt = DateTime.UtcNow
        };
        _db.PullProxies.Add(entity);
        await _db.SaveChangesAsync();

        // 按需拉流（且不录像）：不立即拉流，等播放请求时再拉
        if (enableOnDemand && !enableRecord)
            return (true, null);

        try
        {
            await StartStreamAsync(entity);
        }
        catch (Exception ex)
        {
            if (force)
            {
                // 强制添加：配置已落库，ZLM 启动失败只告警，用户可稍后在详情页「重新拉流」
                _logger.LogWarning(ex, "[PullProxy] 强制添加 {App}/{Stream}：启动拉流失败，保留配置 源={Url}",
                    app, stream, ZlmService.SafeSourceUrl(url));
                return (true, $"已保存配置，但启动拉流失败：{ex.Message}（可稍后在设备详情页重新拉流）");
            }

            // 非强制：回滚刚写入的记录，避免「提示添加失败、重试却报该流已存在」
            _db.PullProxies.Remove(entity);
            await _db.SaveChangesAsync();
            _logger.LogWarning(ex, "[PullProxy] 添加 {App}/{Stream} 失败，已回滚记录 源={Url}",
                app, stream, ZlmService.SafeSourceUrl(url));
            return (false, $"ZLM 添加失败：{ex.Message}");
        }

        // 启用录像：拉流后立即开始录制
        if (enableRecord)
        {
            try
            {
                var rec = await _zlm.StartRecordAsync(app, stream);
                if (rec.Code != 0)
                    _logger.LogWarning("[PullProxy] {App}/{Stream} 启动录像失败 code={Code} msg={Msg}", app, stream, rec.Code, rec.Msg);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PullProxy] {App}/{Stream} 启动录像异常", app, stream);
            }
        }

        _logger.LogInformation("[PullProxy] 已添加 {App}/{Stream} 模式={Mode} 按需={OnDemand} 录像={Record} 源={Url}",
            app, stream, pullMode, enableOnDemand, enableRecord, ZlmService.SafeSourceUrl(url));
        return (true, null);
    }

    public async Task<(bool Ok, string? Error)> DeleteAsync(string vhost, string app, string stream)
    {
        var entity = await _db.PullProxies.FirstOrDefaultAsync(p =>
            p.Vhost == vhost && p.App == app && p.Stream == stream);
        if (entity is null)
            return (false, "记录不存在");

        _db.PullProxies.Remove(entity);
        await _db.SaveChangesAsync();

        // ZLM 删除失败不影响本地记录删除
        await StopStreamAsync(entity);

        _logger.LogInformation("[PullProxy] 已删除 {App}/{Stream} 模式={Mode}", app, stream, entity.PullMode);
        return (true, null);
    }

    /// <summary>
    /// 更新拉流代理（源流地址 / 输出编码 / 取流模式）。
    /// <paramref name="pullMode"/> 传 null 或空表示保持当前取流模式；native ↔ ffmpeg 切换会先断流、
    /// 等旧流从 ZLM 消失后再按新模式重建（ffmpeg 推流遇到「流已存在」会静默失败）。
    /// </summary>
    public async Task<(bool Ok, string? Error)> UpdateAsync(
        string vhost, string app, string stream, string url, int? audioType,
        string outputCodec = "copy", string? pullMode = null)
    {
        if (!IsValidUrl(url))
            return (false, "源流地址必须以 rtsp://、rtmp://、http:// 或 https:// 开头");

        var entity = await _db.PullProxies.FirstOrDefaultAsync(p =>
            p.Vhost == vhost && p.App == app && p.Stream == stream);
        if (entity is null)
            return (false, "记录不存在");

        var oldMode = entity.PullMode;
        var oldUrl = entity.Url;
        var oldCodec = entity.OutputCodec;

        var newMode = pullMode switch
        {
            "ffmpeg" => "ffmpeg",
            "native" => "native",
            _ => oldMode,
        };
        var newCodec = newMode == "ffmpeg" && outputCodec is "h264" or "h265" ? outputCodec : "copy";

        // ZLM 无更新接口，采用「先删后加」更新；先改 ZLM 再落库，失败时把实体回滚，保持 DB 与 ZLM 一致
        try
        {
            await StopStreamAsync(entity);

            if (newMode != oldMode)
            {
                // 停流是异步的：旧任务还在时重建，ffmpeg 模式的推流目标会被「流已存在」拒绝
                await WaitStreamGoneAsync(app, stream, TimeSpan.FromSeconds(20));
            }

            entity.Url = url;
            entity.OutputCodec = newCodec;
            entity.PullMode = newMode;
            entity.AudioType = audioType;
            if (newMode == "native") entity.FfmpegKey = string.Empty;

            await StartStreamAsync(entity);
        }
        catch (Exception ex)
        {
            entity.Url = oldUrl;
            entity.OutputCodec = oldCodec;
            entity.PullMode = oldMode;
            return (false, $"ZLM 更新失败：{ex.Message}");
        }

        await _db.SaveChangesAsync();

        _logger.LogInformation("[PullProxy] 已更新 {App}/{Stream} 模式={Mode}（原 {OldMode}）源={Url} 编码={Codec}",
            app, stream, entity.PullMode, oldMode, ZlmService.SafeSourceUrl(url), entity.OutputCodec);
        return (true, null);
    }

    /// <summary>
    /// 强制重启拉流（先停后拉）：用于流处于离线状态、或需要重建 ZLM 侧拉流任务时的人工干预。
    /// 与 EnsureStreamAsync 不同，这里不会因为「ZLM 里还挂着旧的 media」而跳过。
    /// </summary>
    public async Task<(bool Ok, string? Error)> RestartAsync(string app, string stream)
    {
        var entity = await _db.PullProxies.FirstOrDefaultAsync(p => p.App == app && p.Stream == stream);
        if (entity is null) return (false, "记录不存在");

        await StopStreamAsync(entity);

        // ZLM 删除拉流是异步的：ffmpeg 进程退出、RTMP 会话释放都要时间，
        // 立即重建会被判「流已存在／等待超时」，所以先轮询等流真正从 ZLM 消失
        await WaitStreamGoneAsync(app, stream, TimeSpan.FromSeconds(20));

        Exception? last = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                await StartStreamAsync(entity);
                _logger.LogInformation("[PullProxy] 已重新拉流 {App}/{Stream} 模式={Mode}", app, stream, entity.PullMode);
                return (true, null);
            }
            catch (Exception ex)
            {
                last = ex;
                _logger.LogWarning(ex, "[PullProxy] 重新拉流失败（第 {Attempt}/2 次）{App}/{Stream} 模式={Mode} 源={Url}",
                    attempt, app, stream, entity.PullMode, ZlmService.SafeSourceUrl(entity.Url));
                if (attempt == 1) await Task.Delay(3000);
            }
        }

        return (false, $"重新拉流失败：{last?.Message ?? "ZLM 无响应"}");
    }

    /// <summary>轮询等待 ZLM 里该流消失（停流是异步的，重建前必须先等它真的消失）。</summary>
    private async Task WaitStreamGoneAsync(string app, string stream, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var resp = await _zlm.GetMediaListAsync(null, app, stream);
                if (resp.Code == 0 && resp.Data is not { Count: > 0 }) return;
            }
            catch (Exception ex)
            {
                // ZLM 不可达时不再空等，交给后续 StartStreamAsync 去报错
                _logger.LogDebug(ex, "[PullProxy] 等待流消失时 ZLM 不可用 {App}/{Stream}", app, stream);
                return;
            }

            await Task.Delay(500);
        }
    }

    /// <summary>确保流在线（按需断流的流，播放前动态拉流）。</summary>
    public async Task EnsureStreamAsync(string app, string stream)
    {
        var resp = await _zlm.GetMediaListAsync(null, app, stream);
        if (resp.Code == 0 && resp.Data is { Count: > 0 }) return;

        var entity = await _db.PullProxies.FirstOrDefaultAsync(p => p.App == app && p.Stream == stream);
        if (entity is null) return;

        try
        {
            await StartStreamAsync(entity);
            _logger.LogInformation("[PullProxy] 流已拉起 {App}/{Stream} 模式={Mode}", app, stream, entity.PullMode);
        }
        catch (Exception ex)
        {
            // 按需拉流失败是"点了播放没画面"的常见原因，必须留痕
            _logger.LogWarning(ex, "[PullProxy] 按需拉流失败 {App}/{Stream} 模式={Mode} 源={Url}",
                app, stream, entity.PullMode, ZlmService.SafeSourceUrl(entity.Url));
        }
    }

    /// <summary>按取流模式启动拉流；ffmpeg 模式成功后保存返回的 key。</summary>
    private async Task StartStreamAsync(PullProxy entity)
    {
        if (entity.PullMode == "ffmpeg")
        {
            // ZLM 只接收源地址 + 推流目标，ffmpeg 命令由 config.ini 的 [ffmpeg] cmd 模板生成
            // （本项目模板：视频 copy + 音频转 AAC，供浏览器 HLS/FLV 播放）
            var dst = _zlm.BuildInternalRtmpUrl(entity.App, entity.Stream);
            var resp = await _zlm.AddFFmpegSourceAsync(entity.Url, dst);
            if (resp.Code != 0 || resp.Data?.Key == null)
                throw new Exception(resp.Msg ?? "FFmpeg 拉流失败（请确认 ZLM 容器内已安装 ffmpeg）");

            entity.FfmpegKey = resp.Data.Key;
            await _db.SaveChangesAsync();
            return;
        }

        var proxyResp = await _zlm.AddStreamProxyAsync(entity.Vhost, entity.App, entity.Stream, entity.Url, entity.AudioType);
        if (proxyResp.Code != 0)
            throw new Exception(proxyResp.Msg ?? "ZLM 拉流失败");
    }

    /// <summary>按取流模式停止拉流（删除失败不影响调用方）；按需拉流 watchdog 复用。</summary>
    public async Task StopStreamAsync(PullProxy entity)
    {
        try
        {
            if (entity.PullMode == "ffmpeg")
            {
                var key = string.IsNullOrEmpty(entity.FfmpegKey)
                    ? _zlm.BuildInternalRtmpUrl(entity.App, entity.Stream)
                    : entity.FfmpegKey;
                await _zlm.DelFFmpegSourceAsync(key);
            }
            else
            {
                await _zlm.DelStreamProxyAsync($"{entity.Vhost}/{entity.App}/{entity.Stream}");
            }
        }
        catch (Exception ex)
        {
            // 删除失败不影响本地记录，但 ZLM 侧可能残留拉流，记 Debug 备查
            _logger.LogDebug(ex, "[PullProxy] 停止拉流失败 {App}/{Stream} 模式={Mode}", entity.App, entity.Stream, entity.PullMode);
        }
    }

    /// <summary>切换按需拉流开关。</summary>
    public async Task<(bool Ok, string? Error)> SetOnDemandAsync(string app, string stream, bool enable)
    {
        var entity = await _db.PullProxies.FirstOrDefaultAsync(p => p.App == app && p.Stream == stream);
        if (entity is null) return (false, "记录不存在");

        entity.EnableOnDemand = enable;
        await _db.SaveChangesAsync();

        if (!enable)
        {
            await EnsureStreamAsync(app, stream);
        }
        return (true, null);
    }

    /// <summary>开启/停止录像。</summary>
    public async Task<(bool Ok, string? Error)> SetRecordAsync(string app, string stream, bool enable)
    {
        var entity = await _db.PullProxies.FirstOrDefaultAsync(p => p.App == app && p.Stream == stream);
        if (entity is null) return (false, "记录不存在");

        if (enable)
        {
            await EnsureStreamAsync(app, stream);
            try
            {
                var resp = await _zlm.StartRecordAsync(app, stream);
                if (resp.Code != 0)
                {
                    _logger.LogWarning("[PullProxy] {App}/{Stream} 开始录像被 ZLM 拒绝 code={Code} msg={Msg}", app, stream, resp.Code, resp.Msg);
                    return (false, $"ZLM 开始录像失败：{resp.Msg}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PullProxy] {App}/{Stream} 开始录像异常", app, stream);
                return (false, $"ZLM 连接失败：{ex.Message}");
            }
            entity.EnableRecord = true;
        }
        else
        {
            try { await _zlm.StopRecordAsync(app, stream); } catch { }
            entity.EnableRecord = false;
        }

        await _db.SaveChangesAsync();
        return (true, null);
    }

    /// <summary>
    /// 某日期下的录像文件列表。
    /// 优先查 ZLM on_record_mp4 hook 写入的索引（切片完成即入库），
    /// 无记录时回落到 ZLM listMp4Record（hook 未启用 / 历史文件 / 手工放入的文件）。
    /// </summary>
    public async Task<List<string>> GetRecordFilesAsync(string app, string stream, string period)
    {
        if (DateTime.TryParseExact(period, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var day))
        {
            var from = day.ToUniversalTime();
            var to = from.AddDays(1);

            var rows = await _db.RecordingFiles
                .Where(r => r.App == app && r.Stream == stream &&
                            r.StartTimeUtc >= from && r.StartTimeUtc < to &&
                            r.MissingAtUtc == null)
                .OrderBy(r => r.StartTimeUtc)
                .Select(r => r.FileName)
                .ToListAsync();

            if (rows.Count > 0) return rows;
        }

        var resp = await _zlm.ListMp4RecordAsync(app, stream, period);
        return resp.Code == 0 && resp.Data?.Paths != null ? resp.Data.Paths : new List<string>();
    }

    private async Task<Dictionary<string, PullProxyItem>> LoadProxyMapAsync()
    {
        var map = new Dictionary<string, PullProxyItem>();
        try
        {
            var resp = await _zlm.ListStreamProxyAsync();
            if (resp.Code == 0 && resp.Data != null)
            {
                foreach (var item in resp.Data)
                {
                    if (!string.IsNullOrEmpty(item.Key))
                        map[item.Key] = item;
                }
            }
        }
        catch
        {
            // 忽略，保持空 map
        }
        return map;
    }

    private async Task<Dictionary<string, MediaItem>> LoadMediaMapAsync()
    {
        var map = new Dictionary<string, MediaItem>();
        try
        {
            var resp = await _zlm.GetMediaListAsync();
            if (resp.Code == 0 && resp.Data != null)
            {
                foreach (var media in resp.Data)
                {
                    if (media.Vhost != null && media.App != null && media.Stream != null)
                        map[$"{media.Vhost}/{media.App}/{media.Stream}"] = media;
                }
            }
        }
        catch
        {
            // 忽略，保持空 map
        }
        return map;
    }

    private static string FormatSpeed(long bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return "-";
        if (bytesPerSecond >= 1024 * 1024) return $"{bytesPerSecond / 1024.0 / 1024.0:F2} MB/s";
        return $"{bytesPerSecond / 1024.0:F1} KB/s";
    }

    private static bool IsValidName(string s)
        => Regex.IsMatch(s, "^[a-zA-Z0-9._-]+$");

    private static bool IsValidUrl(string url)
        => url.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase)
           || url.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase)
           || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
