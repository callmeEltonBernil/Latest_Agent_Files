using Microsoft.AspNetCore.Mvc;
using NextHorizon.Models;
using NextHorizon.Filters;
using Microsoft.EntityFrameworkCore;
using System;

namespace NextHorizon.Controllers
{
    [AuthenticationFilter]
    public class AgentController : Controller
    {
        private readonly AppDbContext _context;

        public AgentController(AppDbContext context)
        {
            _context = context;
        }

        public IActionResult HelpCenter()
        {
            ViewBag.AgentName = HttpContext.Session.GetString("FullName") ?? "Agent";
            ViewBag.AgentUsername = HttpContext.Session.GetString("Username") ?? "";
            ViewBag.UserId = HttpContext.Session.GetInt32("UserId") ?? 0;
            return View();
        }

        public async Task<IActionResult> FAQs()
        {
            var faqs = await _context.FAQs.ToListAsync();
            return View(faqs);
        }

        public IActionResult CreateFAQ() => View();

        [HttpPost]
        public async Task<IActionResult> CreateFAQ(FAQ faq)
        {
            if (ModelState.IsValid)
            {
                faq.DateAdded = DateTime.Now;
                faq.LastUpdated = DateTime.Now;
                _context.FAQs.Add(faq);
                await _context.SaveChangesAsync();
                return RedirectToAction("FAQs");
            }
            return View(faq);
        }

        public async Task<IActionResult> EditFAQ(int id)
        {
            var faq = await _context.FAQs.FindAsync(id);
            if (faq == null) return NotFound();
            return View(faq);
        }

        [HttpPost]
        public async Task<IActionResult> EditFAQ(FAQ faq)
        {
            if (ModelState.IsValid)
            {
                faq.LastUpdated = DateTime.Now;
                _context.FAQs.Update(faq);
                await _context.SaveChangesAsync();
                return RedirectToAction("FAQs");
            }
            return View(faq);
        }

        [HttpPost]
        public async Task<IActionResult> DeleteFAQ(int id)
        {
            var faq = await _context.FAQs.FindAsync(id);
            if (faq != null)
            {
                _context.FAQs.Remove(faq);
                await _context.SaveChangesAsync();
            }
            return RedirectToAction("FAQs");
        }

        // ── CONVERSATIONS ─────────────────────────────────────────
        // SupportFAQs is the real conversation table.
        // SupportMessages.ConversationId → SupportFAQs.Id

        [HttpGet]
        public async Task<IActionResult> GetConversations()
        {
            var userId = HttpContext.Session.GetInt32("UserId") ?? 0;
            if (userId == 0)
                return Json(new { success = false, message = "Not logged in." });

            // Pull conversations assigned to this agent OR unassigned (agent can claim)
            var conversations = await _context.SupportFAQs
                .Where(f => f.AgentId == userId || f.AgentId == null)
                .OrderByDescending(f => f.CreatedAt)
                .Select(f => new {
                    id = f.Id,
                    category = f.Category,
                    question = f.Question,
                    status = f.Status,
                    userType = f.UserType,
                    agentId = f.AgentId,
                    createdAt = f.CreatedAt,
                    endTime = f.EndTime,
                    isAssigned = f.AgentId == userId
                })
                .ToListAsync();

            return Json(new { success = true, conversations });
        }

        [HttpGet]
        public async Task<IActionResult> GetMessages(int conversationId)
        {
            var userId = HttpContext.Session.GetInt32("UserId") ?? 0;
            if (userId == 0)
                return Json(new { success = false, message = "Not logged in." });

            // Allow access if assigned to this agent OR unassigned
            var conversation = await _context.SupportFAQs
                .FirstOrDefaultAsync(f => f.Id == conversationId &&
                    (f.AgentId == userId || f.AgentId == null));

            if (conversation == null)
                return Json(new { success = false, message = "Conversation not found or not assigned to you." });

            var messages = await _context.SupportMessages
                .Where(m => m.ConversationId == conversationId)
                .OrderBy(m => m.CreatedAt)
                .Select(m => new {
                    m.Id,
                    m.ConversationId,
                    m.SenderId,
                    m.SenderRole,
                    m.MessageText,
                    m.CreatedAt
                })
                .ToListAsync();

            return Json(new { success = true, messages });
        }

        // Claim an unassigned conversation
        [HttpPost]
        public async Task<IActionResult> ClaimConversation([FromBody] ClaimConversationRequest model)
        {
            var userId = HttpContext.Session.GetInt32("UserId") ?? 0;
            if (userId == 0)
                return Json(new { success = false, message = "Not logged in." });

            var conversation = await _context.SupportFAQs
                .FirstOrDefaultAsync(f => f.Id == model.ConversationId && f.AgentId == null);

            if (conversation == null)
                return Json(new { success = false, message = "Conversation not found or already claimed." });

            conversation.AgentId = userId;
            conversation.StartTime = DateTime.Now;
            await _context.SaveChangesAsync();

            return Json(new { success = true, conversationId = model.ConversationId });
        }

        [HttpPost]
        public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest model)
        {
            var userId = HttpContext.Session.GetInt32("UserId") ?? 0;
            if (userId == 0)
                return Json(new { success = false, message = "Not logged in." });

            if (model == null || model.ConversationId <= 0 || string.IsNullOrWhiteSpace(model.MessageText))
                return BadRequest(new { success = false, message = "Invalid request." });

            // Allow send if assigned to this agent OR unassigned (will auto-claim)
            var conversation = await _context.SupportFAQs
                .FirstOrDefaultAsync(f => f.Id == model.ConversationId &&
                    (f.AgentId == userId || f.AgentId == null));

            if (conversation == null)
                return Json(new { success = false, message = "Conversation not found or not assigned to you." });

            if (string.Equals(conversation.Status, "Resolved", StringComparison.OrdinalIgnoreCase))
                return Json(new { success = false, message = "Conversation is resolved. Unresolve it first to send a message." });

            // Auto-claim if unassigned
            if (conversation.AgentId == null)
            {
                conversation.AgentId = userId;
                conversation.StartTime = DateTime.Now;
            }

            var message = new SupportMessage
            {
                ConversationId = model.ConversationId,
                SenderId = userId,
                SenderRole = "Agent",
                MessageText = model.MessageText,
                CreatedAt = DateTime.Now
            };

            _context.SupportMessages.Add(message);
            await _context.SaveChangesAsync();

            return Json(new
            {
                success = true,
                id = message.Id,
                conversationId = message.ConversationId,
                senderId = message.SenderId,
                senderRole = message.SenderRole,
                messageText = message.MessageText,
                createdAt = message.CreatedAt
            });
        }

        // Resolve a conversation
        [HttpPost]
        public async Task<IActionResult> ResolveConversation([FromBody] ClaimConversationRequest model)
        {
            var userId = HttpContext.Session.GetInt32("UserId") ?? 0;
            if (userId == 0)
                return Json(new { success = false, message = "Not logged in." });

            var conversation = await _context.SupportFAQs
                .FirstOrDefaultAsync(f => f.Id == model.ConversationId && f.AgentId == userId);

            if (conversation == null)
                return Json(new { success = false, message = "Conversation not found." });

            conversation.Status = "Resolved";
            conversation.EndTime = DateTime.Now;
            await _context.SaveChangesAsync();

            return Json(new { success = true, conversationId = model.ConversationId });
        }

        [HttpPost]
        public async Task<IActionResult> EndConversation([FromBody] ClaimConversationRequest model)
        {
            var userId = HttpContext.Session.GetInt32("UserId") ?? 0;
            if (userId == 0)
                return Json(new { success = false, message = "Not logged in." });

            if (model == null || model.ConversationId <= 0)
                return BadRequest(new { success = false, message = "Invalid request." });

            var conversation = await _context.SupportFAQs
                .FirstOrDefaultAsync(f => f.Id == model.ConversationId && f.AgentId == userId);

            if (conversation == null)
                return Json(new { success = false, message = "Conversation not found." });

            // Persist end-of-conversation state in existing SupportFAQs columns
            conversation.Status = "Resolved";
            conversation.EndTime = DateTime.Now;
            await _context.SaveChangesAsync();

            return Json(new { success = true, conversationId = model.ConversationId });
        }

        [HttpPost]
        public async Task<IActionResult> UpdateConversationStatus([FromBody] UpdateConversationStatusRequest model)
        {
            var userId = HttpContext.Session.GetInt32("UserId") ?? 0;
            if (userId == 0)
                return Json(new { success = false, message = "Not logged in." });

            if (model == null || model.ConversationId <= 0 || string.IsNullOrWhiteSpace(model.Status))
                return BadRequest(new { success = false, message = "Invalid request." });

            var conversation = await _context.SupportFAQs
                .FirstOrDefaultAsync(f => f.Id == model.ConversationId && f.AgentId == userId);

            if (conversation == null)
                return Json(new { success = false, message = "Conversation not found." });

            var normalizedStatus = string.Equals(model.Status, "Resolved", StringComparison.OrdinalIgnoreCase)
                ? "Resolved"
                : "Active";

            conversation.Status = normalizedStatus;
            conversation.EndTime = normalizedStatus == "Resolved" ? DateTime.Now : null;
            await _context.SaveChangesAsync();

            return Json(new { success = true, conversationId = model.ConversationId, status = normalizedStatus });
        }

        // ── AGENT STATUS ──────────────────────────────────────────

        [HttpGet]
        public async Task<IActionResult> GetAgentStatus(string agentName)
        {
            var agent = await _context.Agents
                .Where(a => a.AgentName == agentName)
                .OrderByDescending(a => a.StartTime)
                .FirstOrDefaultAsync();

            var status = agent?.AgentStatus ?? "available";
            return Json(new { success = true, status = status });
        }

        [HttpPost]
        public async Task<IActionResult> UpdateAgentStatus([FromBody] UpdateAgentStatusRequest model)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.AgentName) || string.IsNullOrWhiteSpace(model.Status))
                return BadRequest(new { success = false, message = "Invalid request." });

            var agentRecord = await _context.Agents
                .Where(a => a.AgentName == model.AgentName)
                .OrderByDescending(a => a.StartTime)
                .FirstOrDefaultAsync();

            if (agentRecord != null)
            {
                agentRecord.AgentStatus = model.Status;
                await _context.SaveChangesAsync();
                return Json(new { success = true, status = model.Status });
            }

            var newRecord = new Agent
            {
                AgentName = model.AgentName,
                ClientName = "N/A",
                Category = "N/A",
                PreviewQuestion = "N/A",
                ChatSlot = 1,
                ChatStatus = "Active",
                AgentStatus = model.Status,
                StartTime = DateTime.Now
            };

            _context.Agents.Add(newRecord);
            await _context.SaveChangesAsync();
            return Json(new { success = true, status = model.Status });
        }
    }

    public class UpdateAgentStatusRequest
    {
        public string AgentName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }

    public class SendMessageRequest
    {
        public int ConversationId { get; set; }
        public string MessageText { get; set; } = string.Empty;
    }

    public class ClaimConversationRequest
    {
        public int ConversationId { get; set; }
    }

    public class UpdateConversationStatusRequest
    {
        public int ConversationId { get; set; }
        public string Status { get; set; } = string.Empty;
    }
}