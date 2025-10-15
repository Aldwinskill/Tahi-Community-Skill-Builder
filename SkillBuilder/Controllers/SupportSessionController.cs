using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SkillBuilder.Data;
using SkillBuilder.Models;
using SkillBuilder.Services;
using System.Security.Claims;

namespace SkillBuilder.Controllers
{
    [Route("SupportSession")]
    public class SupportSessionController : Controller
    {
        private readonly AppDbContext _context;
        private readonly INotificationService _notificationService;

        public SupportSessionController(AppDbContext context, INotificationService notificationService)
        {
            _context = context;
            _notificationService = notificationService;
        }

        private string? GetUserId() => User.FindFirst("UserId")?.Value;

        // POST: Learner creates a request
        [HttpPost("CreateRequest")]
        public async Task<IActionResult> CreateRequest([FromBody] SupportSessionRequest data)
        {
            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId)) return Unauthorized();

            var request = new SupportSessionRequest
            {
                UserId = userId,
                CourseId = data.CourseId,
                Title = data.Title,
                Message = data.Message,
                SessionDate = data.SessionDate,
                SessionTime = data.SessionTime,
                Status = "Pending",
                CreatedAt = DateTime.UtcNow
            };

            _context.SupportSessionRequests.Add(request);
            await _context.SaveChangesAsync();

            // Include User and Course
            var newRequest = await _context.SupportSessionRequests
                .Include(r => r.User)
                .Include(r => r.Course)
                .FirstOrDefaultAsync(r => r.Id == request.Id);

            if (newRequest == null || newRequest.Course == null || newRequest.User == null)
                return BadRequest("Invalid request details.");

            // ✅ Manually load the Artisan (since Course.CreatedBy maps to Artisan.UserId)
            newRequest.Course.Artisan = await _context.Artisans
                .FirstOrDefaultAsync(a => a.UserId == newRequest.Course.CreatedBy);

            // ✅ Notify Learner
            await AddNotificationAsync(
                newRequest.UserId,
                $"✅ Your support session request for '{newRequest.Course.Title}' has been successfully submitted."
            );

            // ✅ Notify Artisan (only if found)
            if (newRequest.Course.Artisan != null)
            {
                await AddNotificationAsync(
                    newRequest.Course.Artisan.UserId,
                    $"📩 You’ve received a new support session request for '{newRequest.Course.Title}' from {newRequest.User.FirstName} {newRequest.User.LastName}."
                );
            }
            else
            {
                Console.WriteLine($"⚠️ Artisan not found for Course ID: {newRequest.Course.Id}, CreatedBy: {newRequest.Course.CreatedBy}");
            }

            return Ok(new
            {
                success = true,
                message = "Support session request submitted successfully."
            });
        }

        // GET: Artisan’s view of pending support requests
        [HttpGet("PendingRequests")]
        public async Task<IActionResult> PendingRequests()
        {
            var artisanId = GetUserId();
            if (string.IsNullOrEmpty(artisanId)) return Unauthorized();

            var requests = await _context.SupportSessionRequests
                .Include(r => r.User)
                .Include(r => r.Course)
                .Where(r => r.Course.CreatedBy == artisanId && r.Status == "Pending")
                .OrderByDescending(r => r.CreatedAt)
                .AsNoTracking()
                .ToListAsync();

            return View("~/Views/Sections/ArtisanNotebooks/_ArtisanNotebookSupportSessions.cshtml", requests);
        }

        [HttpGet("PendingRequestsPartial")]
        public async Task<IActionResult> PendingRequestsPartial()
        {
            try
            {
                var artisanId = GetUserId();
                if (string.IsNullOrEmpty(artisanId)) return Unauthorized();

                var requests = await _context.SupportSessionRequests
                    .Include(r => r.User)
                    .Include(r => r.Course)
                    .Where(r => r.Course != null &&
                                r.Course.CreatedBy == artisanId &&
                                r.Status == "Pending")
                    .OrderByDescending(r => r.CreatedAt)
                    .AsNoTracking()
                    .ToListAsync();

                return PartialView("~/Views/Shared/Sections/ArtisanNotebooks/SupportSessionsNotebooks/_SupportSessionsNotebookPending.cshtml", requests);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return StatusCode(500, "Internal server error: " + ex.Message);
            }
        }

        public class ConfirmSessionRequest
        {
            public int Id { get; set; }
            public string Platform { get; set; } = "";
            public string Link { get; set; } = "";
        }

        [HttpPost("Confirm")]
        public async Task<IActionResult> ConfirmSession([FromBody] ConfirmSessionRequest data)
        {
            var artisanId = GetUserId();
            if (string.IsNullOrEmpty(artisanId)) return Unauthorized();

            var request = await _context.SupportSessionRequests
                .Include(r => r.User)
                .Include(r => r.Course)
                .FirstOrDefaultAsync(r => r.Id == data.Id && r.Course.CreatedBy == artisanId);

            if (request == null) return NotFound();

            request.Status = "Confirmed";
            request.MeetingPlatform = data.Platform;
            request.MeetingLink = data.Link;
            request.ConfirmedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            // Notify learner
            if (request.User != null)
            {
                await AddNotificationAsync(
                    request.User.Id,
                    $"✅ Your support session for '{request.Course?.Title}' has been confirmed by the artisan."
                );
            }

            // Notify artisan
            await AddNotificationAsync(
                artisanId,
                $"You have confirmed the support session for '{request.Course?.Title}'."
            );

            // Return the full updated object
            return Json(request);
        }

        public class DeclineSessionRequest
        {
            public int Id { get; set; }
            public string? Reason { get; set; }
        }

        [HttpPost("Decline")]
        public async Task<IActionResult> DeclineSession([FromBody] DeclineSessionRequest data)
        {
            var artisanId = GetUserId();
            if (string.IsNullOrEmpty(artisanId)) return Unauthorized();

            var request = await _context.SupportSessionRequests
                .Include(r => r.Course)
                .Include(r => r.User) // include User for JS
                .FirstOrDefaultAsync(r => r.Id == data.Id && r.Course.CreatedBy == artisanId);

            if (request == null) return NotFound();

            request.Status = "Declined";
            await _context.SaveChangesAsync();

            // Notify learner with reschedule action
            if (request.User != null)
            {
                var message = $"❌ Your support session for '{request.Course?.Title}' has been declined by the artisan.";
                if (!string.IsNullOrWhiteSpace(data.Reason))
                    message += $" Reason: {data.Reason}";

                await AddNotificationAsync(
                    request.User.Id,
                    message,
                    "Reschedule Session",
                    $"/Support/RequestSession/{request.Course?.Id}"
                );
            }

            // Notify artisan
            await AddNotificationAsync(
                artisanId,
                $"You have declined the support session for '{request.Course?.Title}'."
            );

            // return the updated request
            return Ok(new
            {
                Id = request.Id,
                Title = request.Title,
                Message = request.Message,
                User = new
                {
                    UserAvatar = request.User?.UserAvatar,
                    FirstName = request.User?.FirstName,
                    LastName = request.User?.LastName
                },
                Course = new
                {
                    Title = request.Course?.Title
                }
            });
        }

        public class CompleteSessionRequest
        {
            public int Id { get; set; }
        }

        [HttpPost("Complete")]
        public async Task<IActionResult> CompleteSession([FromBody] CompleteSessionRequest data)
        {
            var artisanId = GetUserId();
            if (string.IsNullOrEmpty(artisanId)) return Unauthorized();

            var request = await _context.SupportSessionRequests
                .Include(r => r.Course)
                .FirstOrDefaultAsync(r => r.Id == data.Id && r.Course.CreatedBy == artisanId);

            if (request == null) return NotFound();

            request.Status = "Completed";
            request.CompletedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            // Notify learner
            if (request.User != null)
            {
                await AddNotificationAsync(
                    request.User.Id,
                    $"✅ Your support session for '{request.Course?.Title}' has been completed by the artisan."
                );
            }

            // Notify artisan
            await AddNotificationAsync(
                artisanId,
                $"You marked the support session for '{request.Course?.Title}' as completed."
            );

            return Ok(new { success = true });
        }

        [HttpGet("CompletedRequestsPartial")]
        public async Task<IActionResult> CompletedRequestsPartial()
        {
            var artisanId = GetUserId();
            if (string.IsNullOrEmpty(artisanId)) return Unauthorized();

            var requests = await _context.SupportSessionRequests
                .Include(r => r.User)
                .Include(r => r.Course)
                .Where(r => r.Course != null && r.Course.CreatedBy == artisanId && r.Status == "Completed")
                .OrderByDescending(r => r.CompletedAt)
                .AsNoTracking()
                .ToListAsync();

            return PartialView("~/Views/Shared/Sections/ArtisanNotebooks/SupportSessionsNotebooks/_SupportSessionsNotebookCompleted.cshtml", requests);
        }


        [HttpPost("Complete/{id}")]
        public async Task<IActionResult> CompleteSession(int id)
        {
            var artisanId = GetUserId();
            if (string.IsNullOrEmpty(artisanId)) return Unauthorized();

            var request = await _context.SupportSessionRequests
                .Include(r => r.Course)
                .FirstOrDefaultAsync(r => r.Id == id && r.Course.CreatedBy == artisanId);

            if (request == null) return NotFound();

            request.Status = "Completed";
            request.CompletedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            return Ok(new { success = true });
        }

        [HttpPost("Revert/{id}")]
        public async Task<IActionResult> RevertSession(int id)
        {
            var artisanId = GetUserId();
            if (string.IsNullOrEmpty(artisanId)) return Unauthorized();

            var request = await _context.SupportSessionRequests
                .Include(r => r.Course)
                .FirstOrDefaultAsync(r => r.Id == id && r.Course.CreatedBy == artisanId);

            if (request == null) return NotFound();

            request.Status = "Confirmed";
            request.CompletedAt = null;

            await _context.SaveChangesAsync();
            return Ok(new { success = true });
        }

        [HttpGet("DeclinedPartial")]
        public IActionResult DeclinedPartial()
        {
            var artisanId = GetUserId();
            if (string.IsNullOrEmpty(artisanId)) return Unauthorized();

            var declinedSessions = _context.SupportSessionRequests
                .Include(s => s.User)
                .Include(s => s.Course)
                .Where(s => s.Course != null &&
                            s.Course.CreatedBy == artisanId &&
                            s.Status == "Declined")
                .OrderByDescending(s => s.SessionDate)
                .ToList();

            return PartialView("~/Views/Shared/Sections/ArtisanNotebooks/SupportSessionsNotebooks/_SupportSessionsNotebookDeclined.cshtml", declinedSessions);
        }

        [HttpGet("UpcomingPartial")]
        public IActionResult UpcomingPartial()
        {
            var artisanId = GetUserId();
            if (string.IsNullOrEmpty(artisanId)) return Unauthorized();

            var upcomingSessions = _context.SupportSessionRequests
                .Include(s => s.User)
                .Include(s => s.Course)
                .Where(s => s.Course != null &&
                            s.Course.CreatedBy == artisanId &&
                            s.Status == "Confirmed" &&
                            s.SessionDate >= DateTime.UtcNow.Date)
                .OrderBy(s => s.SessionDate)
                .ToList();

            return PartialView("~/Views/Shared/Sections/ArtisanNotebooks/SupportSessionsNotebooks/_SupportSessionsNotebookUpcoming.cshtml", upcomingSessions);
        }

        [HttpGet("GetLatestSessionRequest")]
        public async Task<IActionResult> GetLatestSessionRequest(int courseId)
        {
            var userId = GetUserId();
            if (string.IsNullOrEmpty(userId)) return Unauthorized();

            var latestRequest = await _context.SupportSessionRequests
                .Where(r => r.UserId == userId && r.CourseId == courseId)
                .OrderByDescending(r => r.CreatedAt)
                .FirstOrDefaultAsync();

            return Json(new { status = latestRequest?.Status });
        }

        private async Task AddNotificationAsync(string userId, string message, string? actionText = null, string? actionUrl = null)
        {
            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(message))
                return;

            var notification = new Notification
            {
                UserId = userId,
                Message = message,
                ActionText = actionText,
                ActionUrl = actionUrl,
                CreatedAt = DateTime.UtcNow,
                IsRead = false
            };

            _context.Notifications.Add(notification);
            await _context.SaveChangesAsync();
        }
    }
}
