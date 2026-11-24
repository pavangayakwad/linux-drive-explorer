using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FileExplorer.Api.Controllers;

[ApiController]
[Route("api/config")]
[AllowAnonymous]
public class ConfigController(IConfiguration configuration) : ControllerBase
{
    // Public (pre-login) branding info: the login page needs the app name before anyone is authenticated.
    [HttpGet]
    public IActionResult Get()
    {
        var name = configuration["App:Name"];
        return Ok(new { appName = string.IsNullOrWhiteSpace(name) ? "" : name.Trim() });
    }
}
