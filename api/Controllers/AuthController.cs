using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;

namespace AFHSync.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly IConfiguration _config;

    public AuthController(IConfiguration config)
    {
        _config = config;
    }

    /// <summary>
    /// POST /api/auth/login - Authenticate with username/password from environment variables.
    /// Returns 200 with httpOnly JWT cookie on success, 401 on failure.
    /// Per D-05: simple JWT with env var credentials. D-07: httpOnly cookie. D-08: SameSite=Strict.
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    public IActionResult Login([FromBody] LoginRequest request)
    {
        var adminUsername = _config["Auth:AdminUsername"];
        var adminPassword = _config["Auth:AdminPassword"];

        if (string.IsNullOrEmpty(adminUsername) || string.IsNullOrEmpty(adminPassword))
            return StatusCode(500, new { message = "Auth credentials not configured" });

        if (request.Username != adminUsername || request.Password != adminPassword)
            return Unauthorized(new { message = "Invalid credentials" });

        var token = GenerateJwtToken();

        // Per D-07: httpOnly cookie, D-08: SameSite=Strict
        Response.Cookies.Append("afh_auth", token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = true, // TLS active via nginx
            Expires = DateTimeOffset.UtcNow.AddHours(24), // Per D-06: 24-hour lifetime
            Path = "/"
        });

        return Ok(new { message = "Login successful" });
    }

    /// <summary>
    /// POST /api/auth/logout - Clear the auth cookie.
    /// </summary>
    [HttpPost("logout")]
    [AllowAnonymous]
    public IActionResult Logout()
    {
        Response.Cookies.Delete("afh_auth", new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = false,
            Path = "/"
        });
        return Ok(new { message = "Logged out" });
    }

    /// <summary>
    /// GET /api/auth/me - Returns the authenticated user's identity.
    /// Requires valid JWT (global auth filter applies).
    /// </summary>
    [HttpGet("me")]
    [Authorize]
    public IActionResult Me()
    {
        return Ok(new { username = User.FindFirst(ClaimTypes.Name)?.Value });
    }

    private string GenerateJwtToken()
    {
        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(_config["Auth:JwtSecret"]!));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: "AFHSync",
            audience: "AFHSync",
            claims:
            [
                new Claim(ClaimTypes.Name, _config["Auth:AdminUsername"]!),
                new Claim(ClaimTypes.Role, "admin")
            ],
            expires: DateTime.UtcNow.AddHours(24), // Per D-06: 24-hour lifetime
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public record LoginRequest(string Username, string Password);
