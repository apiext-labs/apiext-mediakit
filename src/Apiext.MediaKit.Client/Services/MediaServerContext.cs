using Apiext.MediaKit.Models;
using Microsoft.JSInterop;

namespace Apiext.MediaKit.Services;

/// <summary>
/// Shared "current media instance" selection across MediaKit pages.
/// Persists the chosen server id in localStorage when JS is available.
/// </summary>
public sealed class MediaServerContext
{
    private const string StorageKey = "apiext.mediakit.currentServerId";
    private readonly MediaKitApiClient _api;
    private readonly IJSRuntime _js;
    private List<MediaServerDto> _servers = [];
    private Guid? _currentId;
    private bool _loaded;

    public MediaServerContext(MediaKitApiClient api, IJSRuntime js)
    {
        _api = api;
        _js = js;
    }

    public event Action? Changed;

    public IReadOnlyList<MediaServerDto> Servers => _servers;
    public Guid? CurrentServerId => _currentId;
    public MediaServerDto? CurrentServer => _servers.FirstOrDefault(x => x.Id == _currentId) ?? _servers.FirstOrDefault();

    public async Task EnsureLoadedAsync()
    {
        if (_loaded && _servers.Count > 0) return;
        await ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        _servers = await _api.GetServersAsync();
        if (!_currentId.HasValue)
        {
            try
            {
                var saved = await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
                if (Guid.TryParse(saved, out var id) && _servers.Any(x => x.Id == id))
                    _currentId = id;
            }
            catch
            {
                // SSR / prerender may not have JS
            }
        }

        if (!_currentId.HasValue || _servers.All(x => x.Id != _currentId))
            _currentId = _servers.FirstOrDefault()?.Id;

        _loaded = true;
        Changed?.Invoke();
    }

    public async Task SetCurrentAsync(Guid? serverId)
    {
        if (serverId.HasValue && _servers.All(x => x.Id != serverId)) return;
        _currentId = serverId ?? _servers.FirstOrDefault()?.Id;
        try
        {
            if (_currentId.HasValue)
                await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, _currentId.Value.ToString());
            else
                await _js.InvokeVoidAsync("localStorage.removeItem", StorageKey);
        }
        catch
        {
            // ignore storage failures
        }
        Changed?.Invoke();
    }
}
