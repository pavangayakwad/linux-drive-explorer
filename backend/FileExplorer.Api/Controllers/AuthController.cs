using System.Security.Claims;
using FileExplorer.Api.Models.Dtos;
using FileExplorer.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FileExplorer.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(IAuthService authService) : ControllerBase
{
    private static readonly HashSet<string> AllowedThemeColors = new(StringComparer.OrdinalIgnoreCase) { "green", "blue", "violet" };

    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var outcome = await authService.LoginAsync(request.Username, request.Password, ct);
        if (outcome.Response is not null)
        {
            return Ok(outcome.Response);
        }
        if (outcome.RetryAfter is { } retryAfter)
        {
            var seconds = (int)Math.Ceiling(retryAfter.TotalSeconds);
            Response.Headers.RetryAfter = seconds.ToString();
            return StatusCode(StatusCodes.Status429TooManyRequests, new { message = $"Too many failed attempts. Try again in {seconds} seconds." });
        }
        return Unauthorized(new { message = "Invalid username or password." });
    }

    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [HttpPost("refresh")]
    public async Task<ActionResult<AuthResponse>> Refresh(RefreshRequest request, CancellationToken ct)
    {
        var result = await authService.RefreshAsync(request.RefreshToken, ct);
        return result is null ? Unauthorized(new { message = "Refresh token is invalid or expired." }) : Ok(result);
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(RefreshRequest request, CancellationToken ct)
    {
        await authService.LogoutAsync(request.RefreshToken, ct);
        return NoContent();
    }

    [Authorize]
    [EnableRateLimiting("auth")]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 8)
        {
            return BadRequest(new { message = "New password must be at least 8 characters long." });
        }
        if (request.NewPassword.Length > 256)
        {
            return BadRequest(new { message = "New password is too long." });
        }

        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized();
        }

        var result = await authService.ChangePasswordAsync(userId, request.CurrentPassword, request.NewPassword, ct);
        return result switch
        {
            ChangePasswordResult.Success => NoContent(),
            ChangePasswordResult.WrongCurrentPassword => BadRequest(new { message = "Current password is incorrect." }),
            _ => Unauthorized(),
        };
    }

    [Authorize]
    [HttpPut("theme")]
    public async Task<IActionResult> UpdateTheme(UpdateThemeRequest request, CancellationToken ct)
    {
        if (!AllowedThemeColors.Contains(request.ThemeColor))
        {
            return BadRequest(new { message = "Unsupported theme color." });
        }

        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized();
        }

        await authService.UpdateThemeColorAsync(userId, request.ThemeColor.ToLowerInvariant(), ct);
        return NoContent();
    }
}
