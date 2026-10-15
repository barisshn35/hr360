using Microsoft.AspNetCore.Mvc;

namespace EngagementService.Infrastructure;

/// <summary>
/// İş kuralı ihlali: web uçları ve sohbet botunun iç uçları aynı çekirdek mantığı
/// paylaştığında hatayı HTTP yanıtına çevirmek için. <c>Code</c> makinece okunur
/// (bot, bilinen durumlar için kendi İngilizce metnini gösterebilir).
/// </summary>
public sealed record RuleError(int Status, string Message, string Code)
{
    public static RuleError Bad(string message, string code) => new(StatusCodes.Status400BadRequest, message, code);
    public static RuleError NotFound(string message, string code = "not_found") => new(StatusCodes.Status404NotFound, message, code);
    public static RuleError Conflict(string message, string code) => new(StatusCodes.Status409Conflict, message, code);

    public IActionResult ToResult() => new ObjectResult(new { message = Message, code = Code }) { StatusCode = Status };
}

/// <summary>İşlemi yapan kişi (web oturumu ya da botun doğruladığı çalışan).</summary>
public sealed record Actor(string UserId, Guid? EmployeeId, string Name);
