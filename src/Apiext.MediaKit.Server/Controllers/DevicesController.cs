using Apiext.MediaKit.Server.Auth;
using Apiext.MediaKit.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace Apiext.MediaKit.Server.Controllers;

/// <summary>统一设备列表（拉流代理 / ONVIF / GB28181），对外 REST API（访问令牌鉴权）。</summary>
[ApiController]
[Route("api/v1/devices")]
[ApiTokenAuth]
public sealed class DevicesController : ControllerBase
{
    private readonly DeviceListService _devices;

    public DevicesController(DeviceListService devices) => _devices = devices;

    /// <summary>统一设备列表（拉流代理 / ONVIF / GB28181）。</summary>
    [HttpGet]
    public async Task<IActionResult> List()
        => Ok(await _devices.ListAsync());
}
