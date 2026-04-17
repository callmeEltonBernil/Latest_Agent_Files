using Microsoft.AspNetCore.Mvc;
using NextHorizon.Models;
using NextHorizon.Filters;
using Microsoft.EntityFrameworkCore;
using System;
using System.Data;
using Microsoft.Extensions.Caching.Memory;

namespace NextHorizon.Controllers
{
    [AuthenticationFilter]
    public class AgentController : Controller
    {
        private readonly AppDbContext _context;
        private readonly IMemoryCache _cache;

        public AgentController(AppDbContext context, IMemoryCache cache)
        {
            _context = context;
            _cache = cache;
        }

        private string GetConversationsCacheKey(int userId) => $"GetConversations_{userId}";
        private void InvalidateConversationsCache(int userId)
        {
            try { _cache?.Remove(GetConversationsCacheKey(userId)); } catch { }
        }

        public IActionResult HelpCenter()
        {
            var fullName = HttpContext.Session.GetString("FullName");
            var username = HttpContext.Session.GetString("Username");
            ViewBag.AgentName = !string.IsNullOrWhiteSpace(fullName) ? fullName : (!string.IsNullOrWhiteSpace(username) ? username : "Agent");
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

        [HttpGet]
        public async Task<IActionResult> GetConversations()
        {
            var userId = HttpContext.Session.GetInt32("UserId") ?? 0;
            if (userId == 0)
                return Json(new { success = false, message = "Not logged in." });

            var convos = await _context.SupportFAQs
                .Where(f => f.AgentId == userId || f.AgentId == null)
                .OrderByDescending(f => f.CreatedAt)
                .ToListAsync();

            var results = new List<object>();

            var convoIds = convos.Select(c => c.Id).ToList();
            var latestAgents = new List<Models.Agent>();
            if (convoIds.Count > 0)
            {
                latestAgents = await _context.Agents
                    .Where(a => a.ConversationID.HasValue && convoIds.Contains(a.ConversationID.Value))
                    .GroupBy(a => a.ConversationID.Value)
                    .Select(g => g.OrderByDescending(a => a.ChatID).FirstOrDefault())
                    .ToListAsync();
            }

            var latestByConvo = latestAgents
                .Where(a => a != null && a.ConversationID.HasValue)
                .ToDictionary(a => a.ConversationID.Value, a => a);

            foreach (var f in convos)
            {
                DateTime? acwStart = null;
                DateTime? acwEnd = null;
                if (latestByConvo.TryGetValue(f.Id, out var agentRow) && agentRow != null)
                {
                    acwStart = agentRow.ACWStartTime;
                    acwEnd = agentRow.ACWEndTime;
                }

                if (acwStart.HasValue && !acwEnd.HasValue)
                {
                    var cap = acwStart.Value.AddMinutes(2);
                    if (DateTime.Now > cap)
                    {
                        acwEnd = cap;
                        try
                        {
                            await _context.Database.ExecuteSqlRawAsync(
                                "UPDATE dbo.Agents SET ACWEndTime = {0} WHERE ConversationID = {1} AND (ACWEndTime IS NULL OR ACWEndTime < ACWStartTime)",
                                acwEnd, f.Id);
                        }
                        catch { }
                    }
                }

                results.Add(new
                {
                    id = f.Id,
                    category = f.Category,
                    question = f.Question,
                    status = f.Status,
                    userType = f.UserType,
                    agentId = f.AgentId,
                    createdAt = f.CreatedAt,
                    startTime = f.StartTime,
                    endTime = f.EndTime,
                    acwStart = acwStart,
                    acwEnd = acwEnd,
                    isAssigned = f.AgentId == userId
                });
            }

            return Json(new { success = true, conversations = results });
        }

        [HttpGet]
        public async Task<IActionResult> GetMessages(int conversationId)
        {
            var userId = HttpContext.Session.GetInt32("UserId") ?? 0;
            if (userId == 0)
                return Json(new { success = false, message = "Not logged in." });

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

        [HttpPost]
        public async Task<IActionResult> ClaimConversation([FromBody] ClaimConversationRequest model)
        {
            var userId = HttpContext.Session.GetInt32("UserId") ?? 0;
            if (userId == 0)
                return Json(new { success = false, message = "Not logged in." });

            if (model == null || model.ConversationId <= 0)
                return BadRequest(new { success = false, message = "Invalid request." });

            var conversation = await _context.SupportFAQs
                .FirstOrDefaultAsync(f => f.Id == model.ConversationId && f.AgentId == null);

            if (conversation == null)
                return Json(new { success = false, message = "Conversation not found or already claimed." });

            conversation.AgentId = userId;
            conversation.StartTime = DateTime.Now;
            await _context.SaveChangesAsync();
            await SetConversationChatStatusAsync(userId, HttpContext.Session.GetString("FullName") ?? string.Empty, model.ConversationId, "In Chat", null, null);
            InvalidateConversationsCache(userId);

            return Json(new { success = true, conversationId = model.ConversationId, endTime = conversation.EndTime });
        }

        [HttpPost]
        public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest model)
        {
            var userId = HttpContext.Session.GetInt32("UserId") ?? 0;
            if (userId == 0)
                return Json(new { success = false, message = "Not logged in." });

            if (model == null || model.ConversationId <= 0 || string.IsNullOrWhiteSpace(model.MessageText))
                return BadRequest(new { success = false, message = "Invalid request." });

            var conversation = await _context.SupportFAQs
                .FirstOrDefaultAsync(f => f.Id == model.ConversationId && f.AgentId == userId);

            if (conversation == null)
                return Json(new { success = false, message = "Conversation not found or not assigned to you." });

            if (string.Equals(conversation.Status, "Resolved", StringComparison.OrdinalIgnoreCase))
                return Json(new { success = false, message = "Conversation is resolved. Unresolve it first to send a message." });

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
            InvalidateConversationsCache(userId);

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
            var agentName = HttpContext.Session.GetString("FullName") ?? string.Empty;
            await UpdateAcwTrackingAsync(model.ConversationId, null, agentName, userId, true);
            await SetConversationChatStatusAsync(userId, agentName, model.ConversationId, "ACW", null, null);
            InvalidateConversationsCache(userId);

            return Json(new { success = true, conversationId = model.ConversationId, endTime = conversation.EndTime });
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

            conversation.Status = "Resolved";
            conversation.EndTime = DateTime.Now;
            await _context.SaveChangesAsync();
            var agentName = HttpContext.Session.GetString("FullName") ?? string.Empty;
            await UpdateAcwTrackingAsync(model.ConversationId, null, agentName, userId, true);
            await SetConversationChatStatusAsync(userId, agentName, model.ConversationId, "ACW", null, null);

            InvalidateConversationsCache(userId);

            var latestAgentRow = await _context.Agents
                .Where(a => a.ConversationID == model.ConversationId)
                .OrderByDescending(a => a.ChatID)
                .FirstOrDefaultAsync();

            return Json(new
            {
                success = true,
                conversationId = model.ConversationId,
                endTime = conversation.EndTime,
                acwStart = latestAgentRow?.ACWStartTime,
                acwEnd = latestAgentRow?.ACWEndTime
            });
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

            if (string.Equals(conversation.Status, "Resolved", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(normalizedStatus, "Resolved", StringComparison.OrdinalIgnoreCase))
            {
                return Json(new { success = false, message = "Resolved conversations cannot be un-resolved." });
            }

            conversation.Status = normalizedStatus;
            conversation.EndTime = normalizedStatus == "Resolved" ? DateTime.Now : null;
            await _context.SaveChangesAsync();

            var chatStatus = normalizedStatus == "Resolved" ? "ACW" : "In Chat";
            var agentName = HttpContext.Session.GetString("FullName") ?? string.Empty;
            if (normalizedStatus == "Resolved")
                await UpdateAcwTrackingAsync(model.ConversationId, null, agentName, userId, true);
            else
                await UpdateAcwTrackingAsync(model.ConversationId, null, agentName, userId, false);

            await SetConversationChatStatusAsync(userId, agentName, model.ConversationId, chatStatus, null, null);

            InvalidateConversationsCache(userId);

            var latestAgentRow = await _context.Agents
                .Where(a => a.ConversationID == model.ConversationId)
                .OrderByDescending(a => a.ChatID)
                .FirstOrDefaultAsync();

            DateTime? acwStart = latestAgentRow?.ACWStartTime;
            DateTime? acwEnd = latestAgentRow?.ACWEndTime;

            if (acwStart.HasValue && !acwEnd.HasValue)
            {
                var cap = acwStart.Value.AddMinutes(2);
                if (DateTime.Now > cap)
                {
                    acwEnd = cap;
                    try
                    {
                        await _context.Database.ExecuteSqlRawAsync(
                            "UPDATE dbo.Agents SET ACWEndTime = {0} WHERE ConversationID = {1} AND (ACWEndTime IS NULL OR ACWEndTime < ACWStartTime)",
                            acwEnd, model.ConversationId);
                    }
                    catch { }
                }
            }

            return Json(new
            {
                success = true,
                conversationId = model.ConversationId,
                status = normalizedStatus,
                endTime = conversation.EndTime,
                acwStart,
                acwEnd
            });
        }

        // ── AGENT STATUS ──────────────────────────────────────────

        [HttpGet]
        public async Task<IActionResult> GetAgentStatus(string agentName)
        {
            var userId = HttpContext.Session.GetInt32("UserId") ?? 0;
            var resolvedAgentName = ResolveAgentName(agentName);

            var agent = await _context.Agents
                .Where(a => (userId != 0 && a.AgentID == userId) || a.AgentName == resolvedAgentName)
                .OrderByDescending(a => a.ChatID)
                .FirstOrDefaultAsync();

            var status = ToUiAgentStatus(agent?.AgentStatus ?? "Available");
            return Json(new { success = true, status = status });
        }

        [HttpGet]
        public async Task<IActionResult> GetConversationAcw(int conversationId)
        {
            var userId = HttpContext.Session.GetInt32("UserId") ?? 0;
            if (userId == 0)
                return Json(new { success = false, message = "Not logged in." });

            if (conversationId <= 0)
                return BadRequest(new { success = false, message = "Invalid conversation id." });

            var latestAgentRow = await _context.Agents
                .Where(a => a.ConversationID == conversationId)
                .OrderByDescending(a => a.ChatID)
                .FirstOrDefaultAsync();

            return Json(new
            {
                success = true,
                acwStart = latestAgentRow?.ACWStartTime,
                acwEnd = latestAgentRow?.ACWEndTime
            });
        }

        [HttpPost]
        public async Task<IActionResult> UpdateAgentStatus([FromBody] UpdateAgentStatusRequest model)
        {
            try
            {
                if (model == null || string.IsNullOrWhiteSpace(model.Status))
                    return BadRequest(new { success = false, message = "Invalid request." });

                var userId = HttpContext.Session.GetInt32("UserId") ?? model.AgentUserId;
                var resolvedAgentName = ResolveAgentName(model.AgentName);
                userId = await ResolveAgentUserIdAsync(userId, resolvedAgentName);

                if (!IsUsableAgentName(resolvedAgentName) && userId == 0)
                    return BadRequest(new { success = false, message = "Agent identity is required." });

                if (model.IsChatStatus)
                {
                    if (model.ConversationId <= 0)
                        return BadRequest(new { success = false, message = "Conversation is required for chat status updates." });

                    var normalizedChatStatus = NormalizeChatStatus(model.Status);

                    if (string.Equals(normalizedChatStatus, "ACW", StringComparison.OrdinalIgnoreCase))
                    {
                        var convo = await _context.SupportFAQs
                            .AsNoTracking()
                            .FirstOrDefaultAsync(f => f.Id == model.ConversationId);

                        if (convo == null)
                            return Json(new { success = false, message = "Conversation not found." });

                        if (!string.Equals(convo.Status, "Resolved", StringComparison.OrdinalIgnoreCase))
                            return Json(new { success = false, message = "ACW can only start after chat is resolved." });
                    }

                    // ── ACW → Available: stamp ACWEndTime, clear ConversationID ──────
                    if (string.Equals(normalizedChatStatus, "Available", StringComparison.OrdinalIgnoreCase))
                    {
                        var now = DateTime.Now;

                        // Stamp ACWEndTime in dbo.Agents
                        await _context.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('dbo.Agents', 'U') IS NOT NULL
BEGIN
    IF COL_LENGTH('dbo.Agents', 'ACWEndTime') IS NOT NULL
    BEGIN
        UPDATE dbo.Agents
        SET ACWEndTime = ISNULL(ACWEndTime, {0})
        WHERE ConversationID = {1}
          AND (ACWEndTime IS NULL OR ACWEndTime < ACWStartTime);
    END
END", now, model.ConversationId);

                        // Stamp ACWEndTime + clear ConversationID in ChatSlot tables
                        for (var slot = 1; slot <= 3; slot++)
                        {
                            var tableName = $"dbo.ChatSlot_{slot}";
                            var slotSql = $@"
IF OBJECT_ID('{tableName}', 'U') IS NOT NULL
BEGIN
    IF COL_LENGTH('{tableName}', 'ConversationID') IS NOT NULL
    BEGIN
        IF COL_LENGTH('{tableName}', 'ACWEndTime') IS NOT NULL
        BEGIN
            UPDATE {tableName}
            SET ACWEndTime = ISNULL(ACWEndTime, {{0}}),
                ChatStatus  = 'Available',
                ConversationID = NULL
            WHERE ConversationID = {{1}}
              AND ({{2}} <= 0 OR AgentId = {{2}});

            IF COL_LENGTH('{tableName}', 'LastUpdatedAt') IS NOT NULL
            BEGIN
                UPDATE {tableName}
                SET LastUpdatedAt = GETDATE()
                WHERE ({{2}} <= 0 OR AgentId = {{2}})
                  AND ConversationID IS NULL;
            END
        END
        ELSE
        BEGIN
            UPDATE {tableName}
            SET ChatStatus  = 'Available',
                ConversationID = NULL
            WHERE ConversationID = {{1}}
              AND ({{2}} <= 0 OR AgentId = {{2}});
        END
    END
END";
                            await _context.Database.ExecuteSqlRawAsync(slotSql, now, model.ConversationId, userId);
                        }

                        // Clear ConversationID on dbo.Agents rows and set ChatStatus = Available
                        var agentRows = await _context.Agents
                            .Where(a => a.ConversationID == model.ConversationId
                                && (userId <= 0 || a.AgentID == userId))
                            .ToListAsync();

                        foreach (var row in agentRows)
                        {
                            row.ConversationID = null;
                            row.ChatStatus = "Available";
                            row.AgentStatus = "Available";
                        }

                        if (agentRows.Count > 0)
                            await _context.SaveChangesAsync();

                        return Json(new
                        {
                            success = true,
                            status = "Available",
                            acwEnded = true,
                            acwEndTime = now
                        });
                    }
                    // ── End ACW → Available special case ───────────────────────────

                    await SetConversationChatStatusAsync(userId, resolvedAgentName, model.ConversationId, normalizedChatStatus, model.ChatSlot, null);

                    try
                    {
                        await UpdateAcwTrackingAsync(model.ConversationId, model.ChatSlot, resolvedAgentName, userId,
                            string.Equals(normalizedChatStatus, "ACW", StringComparison.OrdinalIgnoreCase));
                        if (model.ChatSlot.HasValue)
                            await UpdateChatSlotAcwTimesAsync(model.ChatSlot.Value, model.ConversationId, resolvedAgentName, userId,
                                string.Equals(normalizedChatStatus, "ACW", StringComparison.OrdinalIgnoreCase));
                    }
                    catch { }

                    InvalidateConversationsCache(userId);
                    return Json(new { success = true, status = normalizedChatStatus });
                }

                // ── Agent-level status update ──────────────────────────
                var normalizedAgentStatus = NormalizeAgentStatus(model.Status);

                var hasActiveChat = await _context.Agents
                    .AnyAsync(a => userId != 0 && a.AgentID == userId && a.ChatStatus == "Active");

                if (hasActiveChat && normalizedAgentStatus != "Available")
                    return Json(new { success = false, message = "Cannot change agent status while a conversation is Active." });

                await UpdateAllChatSlotAgentStatusAsync(resolvedAgentName, userId, normalizedAgentStatus);
                await UpsertAgentStatusRowsAsync(userId, resolvedAgentName, normalizedAgentStatus);

                var derivedChatStatus = AgentStatusToChatStatus(normalizedAgentStatus);
                if (derivedChatStatus != null)
                {
                    for (int slot = 1; slot <= 3; slot++)
                    {
                        var tableName = $"dbo.ChatSlot_{slot}";
                        var sql = $@"
IF OBJECT_ID('{tableName}', 'U') IS NOT NULL
BEGIN
    IF COL_LENGTH('{tableName}', 'ChatStatus') IS NOT NULL
    BEGIN
        UPDATE {tableName}
        SET ChatStatus = {{0}}
        WHERE ({{1}} > 0 AND AgentId = {{1}})
           OR ({{1}} <= 0 AND LOWER(LTRIM(RTRIM(ISNULL(AgentName,'')))) = LOWER(LTRIM(RTRIM({{2}}))));
        IF COL_LENGTH('{tableName}', 'ChatStatusLastUpdatedAt') IS NOT NULL
        BEGIN
            UPDATE {tableName}
            SET ChatStatusLastUpdatedAt = GETDATE()
            WHERE ({{1}} > 0 AND AgentId = {{1}})
               OR ({{1}} <= 0 AND LOWER(LTRIM(RTRIM(ISNULL(AgentName,'')))) = LOWER(LTRIM(RTRIM({{2}}))));
        END
    END
END";
                        await _context.Database.ExecuteSqlRawAsync(sql, derivedChatStatus, userId, resolvedAgentName);
                    }
                }

                return Json(new { success = true, status = ToUiAgentStatus(normalizedAgentStatus) });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Failed to update status.", detail = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> UpdateAgentStatusDirect(string status, int agentUserId, string agentName)
        {
            if (string.IsNullOrWhiteSpace(status))
                return BadRequest(new { success = false, message = "Invalid request." });

            var normalizedAgentStatus = NormalizeAgentStatus(status);
            var resolvedAgentName = ResolveAgentName(agentName);
            var userId = await ResolveAgentUserIdAsync(agentUserId, resolvedAgentName);

            try
            {
                await UpdateAllChatSlotAgentStatusAsync(resolvedAgentName, userId, normalizedAgentStatus);
                await UpsertAgentStatusRowsAsync(userId, resolvedAgentName, normalizedAgentStatus);
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Failed to update agent status directly.", detail = ex.Message });
            }

            return Json(new { success = true, status = ToUiAgentStatus(normalizedAgentStatus) });
        }

        [HttpPost]
        public async Task<IActionResult> SaveConversationNotes([FromBody] SaveConversationNotesRequest model)
        {
            var userId = HttpContext.Session.GetInt32("UserId") ?? model.AgentUserId;
            if (userId == 0)
                return Json(new { success = false, message = "Not logged in." });

            if (model == null || model.ConversationId <= 0)
                return BadRequest(new { success = false, message = "Invalid request." });

            var agentName = ResolveAgentName(model.AgentName);
            userId = await ResolveAgentUserIdAsync(userId, agentName);

            await UpdateAgentNotesAsync(userId, agentName, model.ConversationId, model.ChatSlot, model.Notes ?? string.Empty);
            InvalidateConversationsCache(userId);
            return Json(new { success = true });
        }

        // ── PRIVATE HELPERS ───────────────────────────────────────

        private string ResolveAgentName(string requestedAgentName)
        {
            var sessionName = HttpContext.Session.GetString("FullName") ?? string.Empty;
            if (IsUsableAgentName(sessionName)) return sessionName;
            if (IsUsableAgentName(requestedAgentName)) return requestedAgentName;
            var username = HttpContext.Session.GetString("Username") ?? string.Empty;
            if (IsUsableAgentName(username)) return username;
            return sessionName;
        }

        private static bool IsUsableAgentName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            return !string.Equals(value.Trim(), "Agent", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeAgentStatus(string status)
        {
            return (status?.Trim().ToLowerInvariant() ?? "available") switch
            {
                "available" => "Available",
                "break" => "Break",
                "lunch" => "Lunch",
                "eos" => "EOS",
                _ => "Available"
            };
        }

        private static string ToUiAgentStatus(string status)
        {
            return (status?.Trim().ToLowerInvariant() ?? "available") switch
            {
                "available" => "available",
                "break" => "break",
                "lunch" => "lunch",
                "eos" => "eos",
                _ => "available"
            };
        }

        private static string NormalizeChatStatus(string status)
        {
            return (status?.Trim().ToLowerInvariant() ?? "available") switch
            {
                "inchat" => "Active",
                "active" => "Active",
                "available" => "Available",
                "unavailable" => "Unavailable",
                "break" => "Unavailable",
                "lunch" => "Unavailable",
                "eos" => "Unavailable",
                "acw" => "ACW",
                _ => "Available"
            };
        }

        private static string NormalizeAgentsTableChatStatus(string chatStatus)
        {
            return (chatStatus?.Trim().ToLowerInvariant() ?? "available") switch
            {
                "inchat" => "Active",
                "active" => "Active",
                "available" => "Available",
                "unavailable" => "Unavailable",
                "break" => "Unavailable",
                "lunch" => "Unavailable",
                "eos" => "Unavailable",
                "acw" => "Unavailable",
                "resolved" => "Unavailable",
                _ => "Available"
            };
        }

        private static string MapChatStatusToAgentStatus(string chatStatus)
        {
            return (chatStatus?.Trim().ToLowerInvariant() ?? "available") switch
            {
                "available" => "Available",
                "break" => "Unavailable",
                "lunch" => "Unavailable",
                "eos" => "Unavailable",
                "unavailable" => "Unavailable",
                "acw" => "Unavailable",
                "inchat" => "Active",
                "active" => "Active",
                _ => "Available"
            };
        }

        private static string? AgentStatusToChatStatus(string agentStatus)
        {
            return agentStatus?.Trim().ToLowerInvariant() switch
            {
                "break" => "Unavailable",
                "lunch" => "Unavailable",
                "eos" => "Unavailable",
                "available" => "Available",
                _ => null
            };
        }

        private async Task SetConversationChatStatusAsync(int userId, string agentName, int conversationId, string chatStatus, int? chatSlot, string? notes)
        {
            var resolvedAgentName = !string.IsNullOrWhiteSpace(agentName)
                ? agentName
                : (HttpContext.Session.GetString("FullName") ?? string.Empty);
            var persistedAgentChatStatus = NormalizeAgentsTableChatStatus(chatStatus);

            var rows = await _context.Agents
                .Where(a => a.ConversationID == conversationId)
                .OrderByDescending(a => a.ChatID)
                .ToListAsync();

            if (rows.Count == 0)
            {
                var fallbackAgentName = ResolvePersistedAgentName(resolvedAgentName, userId);
                if (!string.IsNullOrWhiteSpace(fallbackAgentName))
                {
                    var conversation = await _context.SupportFAQs
                        .AsNoTracking()
                        .FirstOrDefaultAsync(f => f.Id == conversationId);

                    var latestAgent = await _context.Agents
                        .Where(a => (userId != 0 && a.AgentID == userId) || a.AgentName == fallbackAgentName)
                        .OrderByDescending(a => a.ChatID)
                        .FirstOrDefaultAsync();

                    var createdRow = new Agent
                    {
                        AgentID = userId > 0 ? userId : null,
                        ConversationID = conversationId,
                        AgentName = fallbackAgentName,
                        ClientName = conversation?.UserType ?? "N/A",
                        Category = conversation?.Category ?? "N/A",
                        PreviewQuestion = conversation?.Question ?? "Status update",
                        ChatSlot = chatSlot.HasValue && chatSlot.Value >= 1 && chatSlot.Value <= 3 ? chatSlot.Value : 1,
                        ChatStatus = persistedAgentChatStatus,
                        AgentStatus = MapChatStatusToAgentStatus(persistedAgentChatStatus)
                            ?? (string.IsNullOrWhiteSpace(latestAgent?.AgentStatus) ? "Available" : latestAgent.AgentStatus)
                    };

                    _context.Agents.Add(createdRow);
                    await _context.SaveChangesAsync();
                    rows.Add(createdRow);
                }
            }

            var shouldSave = false;
            foreach (var row in rows)
            {
                if (!string.Equals(row.ChatStatus, persistedAgentChatStatus, StringComparison.OrdinalIgnoreCase))
                {
                    row.ChatStatus = persistedAgentChatStatus;
                    shouldSave = true;
                }

                var mappedAgentStatus = MapChatStatusToAgentStatus(persistedAgentChatStatus);
                if (!string.Equals(row.AgentStatus, mappedAgentStatus, StringComparison.OrdinalIgnoreCase))
                {
                    row.AgentStatus = mappedAgentStatus;
                    shouldSave = true;
                }

                if (row.ConversationID != conversationId)
                {
                    row.ConversationID = conversationId;
                    shouldSave = true;
                }

                if (chatSlot.HasValue && chatSlot.Value >= 1 && chatSlot.Value <= 3 && row.ChatSlot != chatSlot.Value)
                {
                    row.ChatSlot = chatSlot.Value;
                    shouldSave = true;
                }
            }

            if (shouldSave) await _context.SaveChangesAsync();

            var targetSlot = chatSlot;
            if ((!targetSlot.HasValue || targetSlot.Value < 1 || targetSlot.Value > 3) && rows.Count > 0)
                targetSlot = rows[0].ChatSlot;

            if (targetSlot.HasValue && targetSlot.Value >= 1 && targetSlot.Value <= 3)
            {
                await UpdateChatSlotStatusAsync(targetSlot.Value, resolvedAgentName, userId, conversationId, persistedAgentChatStatus);
                if (notes != null)
                    await UpdateChatSlotNotesAsync(targetSlot.Value, resolvedAgentName ?? string.Empty, userId, conversationId, notes ?? string.Empty);
            }
        }

        private async Task UpdateAllChatSlotAgentStatusAsync(string agentName, int userId, string agentStatus)
        {
            for (var slot = 1; slot <= 3; slot++)
            {
                var tableName = $"dbo.ChatSlot_{slot}";
                var sql = $@"
IF OBJECT_ID('{tableName}', 'U') IS NOT NULL
BEGIN
    IF COL_LENGTH('{tableName}', 'LastUpdatedAt') IS NOT NULL
    BEGIN
        UPDATE {tableName}
        SET AgentStatus = {{0}},
            LastUpdatedAt = GETDATE()
        WHERE ({{2}} > 0 AND AgentId = {{2}})
           OR ({{2}} <= 0 AND (
                LOWER(LTRIM(RTRIM(ISNULL(AgentName, '')))) = LOWER(LTRIM(RTRIM({{1}})))
                OR LOWER(ISNULL(AgentName, '')) LIKE '%' + LOWER(LTRIM(RTRIM({{1}}))) + '%'
           ));
    END
    ELSE
    BEGIN
        UPDATE {tableName}
        SET AgentStatus = {{0}}
        WHERE ({{2}} > 0 AND AgentId = {{2}})
           OR ({{2}} <= 0 AND (
                LOWER(LTRIM(RTRIM(ISNULL(AgentName, '')))) = LOWER(LTRIM(RTRIM({{1}})))
                OR LOWER(ISNULL(AgentName, '')) LIKE '%' + LOWER(LTRIM(RTRIM({{1}}))) + '%'
           ));
    END

    IF COL_LENGTH('{tableName}', 'AgentStatusLastUpdatedAt') IS NOT NULL
    BEGIN
        UPDATE {tableName}
        SET AgentStatusLastUpdatedAt = GETDATE()
        WHERE ({{2}} > 0 AND AgentId = {{2}})
           OR ({{2}} <= 0 AND (
                LOWER(LTRIM(RTRIM(ISNULL(AgentName, '')))) = LOWER(LTRIM(RTRIM({{1}})))
                OR LOWER(ISNULL(AgentName, '')) LIKE '%' + LOWER(LTRIM(RTRIM({{1}}))) + '%'
           ));
    END
END";
                await _context.Database.ExecuteSqlRawAsync(sql, agentStatus, agentName, userId);
            }
        }

        private async Task UpdateChatSlotStatusAsync(int slot, string agentName, int userId, int conversationId, string chatStatus)
        {
            if (slot < 1 || slot > 3) return;

            var tableName = $"dbo.ChatSlot_{slot}";
            var sql = $@"
IF OBJECT_ID('{tableName}', 'U') IS NOT NULL
BEGIN
    IF COL_LENGTH('{tableName}', 'ConversationID') IS NOT NULL
    BEGIN
        IF COL_LENGTH('{tableName}', 'LastUpdatedAt') IS NOT NULL
        BEGIN
            UPDATE {tableName}
            SET ChatStatus = {{0}},
                LastUpdatedAt = GETDATE()
            WHERE ConversationID = {{3}};
        END
        ELSE
        BEGIN
            UPDATE {tableName}
            SET ChatStatus = {{0}}
            WHERE ConversationID = {{3}};
        END
    END
    ELSE
    BEGIN
        IF COL_LENGTH('{tableName}', 'LastUpdatedAt') IS NOT NULL
        BEGIN
            UPDATE {tableName}
            SET ChatStatus = {{0}},
                LastUpdatedAt = GETDATE()
            WHERE ({{2}} > 0 AND AgentId = {{2}})
               OR ({{2}} <= 0 AND (
                    LOWER(LTRIM(RTRIM(ISNULL(AgentName, '')))) = LOWER(LTRIM(RTRIM({{1}})))
                    OR LOWER(ISNULL(AgentName, '')) LIKE '%' + LOWER(LTRIM(RTRIM({{1}}))) + '%'
               ));
        END
        ELSE
        BEGIN
            UPDATE {tableName}
            SET ChatStatus = {{0}}
            WHERE ({{2}} > 0 AND AgentId = {{2}})
               OR ({{2}} <= 0 AND (
                    LOWER(LTRIM(RTRIM(ISNULL(AgentName, '')))) = LOWER(LTRIM(RTRIM({{1}})))
                    OR LOWER(ISNULL(AgentName, '')) LIKE '%' + LOWER(LTRIM(RTRIM({{1}}))) + '%'
               ));
        END
    END

    IF COL_LENGTH('{tableName}', 'ChatStatusLastUpdatedAt') IS NOT NULL
    BEGIN
        IF COL_LENGTH('{tableName}', 'ConversationID') IS NOT NULL
        BEGIN
            UPDATE {tableName}
            SET ChatStatusLastUpdatedAt = GETDATE()
            WHERE ConversationID = {{3}};
        END
        ELSE
        BEGIN
            UPDATE {tableName}
            SET ChatStatusLastUpdatedAt = GETDATE()
            WHERE ({{2}} > 0 AND AgentId = {{2}})
               OR ({{2}} <= 0 AND (
                    LOWER(LTRIM(RTRIM(ISNULL(AgentName, '')))) = LOWER(LTRIM(RTRIM({{1}})))
                    OR LOWER(ISNULL(AgentName, '')) LIKE '%' + LOWER(LTRIM(RTRIM({{1}}))) + '%'
               ));
        END
    END
END";
            await _context.Database.ExecuteSqlRawAsync(sql, chatStatus, agentName ?? string.Empty, userId, conversationId);
        }

        private async Task UpdateChatSlotNotesAsync(int slot, string agentName, int userId, int conversationId, string notes)
        {
            if (slot < 1 || slot > 3) return;

            var tableName = $"dbo.ChatSlot_{slot}";
            var sql = $@"
IF OBJECT_ID('{tableName}', 'U') IS NOT NULL
BEGIN
    IF COL_LENGTH('{tableName}', 'Notes') IS NOT NULL
    BEGIN
        IF COL_LENGTH('{tableName}', 'ConversationID') IS NOT NULL
        BEGIN
            IF COL_LENGTH('{tableName}', 'LastUpdatedAt') IS NOT NULL
            BEGIN
                UPDATE {tableName}
                SET Notes = {{0}},
                    LastUpdatedAt = GETDATE()
                WHERE ConversationID = {{3}};
            END
            ELSE
            BEGIN
                UPDATE {tableName}
                SET Notes = {{0}}
                WHERE ConversationID = {{3}};
            END
        END
        ELSE
        BEGIN
            IF COL_LENGTH('{tableName}', 'LastUpdatedAt') IS NOT NULL
            BEGIN
                UPDATE {tableName}
                SET Notes = {{0}},
                    LastUpdatedAt = GETDATE()
                WHERE ({{2}} > 0 AND AgentId = {{2}})
                   OR ({{2}} <= 0 AND (
                        LOWER(LTRIM(RTRIM(ISNULL(AgentName, '')))) = LOWER(LTRIM(RTRIM({{1}})))
                        OR LOWER(ISNULL(AgentName, '')) LIKE '%' + LOWER(LTRIM(RTRIM({{1}}))) + '%'
                   ));
            END
            ELSE
            BEGIN
                UPDATE {tableName}
                SET Notes = {{0}}
                WHERE ({{2}} > 0 AND AgentId = {{2}})
                   OR ({{2}} <= 0 AND (
                        LOWER(LTRIM(RTRIM(ISNULL(AgentName, '')))) = LOWER(LTRIM(RTRIM({{1}})))
                        OR LOWER(ISNULL(AgentName, '')) LIKE '%' + LOWER(LTRIM(RTRIM({{1}}))) + '%'
                   ));
            END
        END
    END

    IF COL_LENGTH('{tableName}', 'NotesLastUpdatedAt') IS NOT NULL
    BEGIN
        IF COL_LENGTH('{tableName}', 'ConversationID') IS NOT NULL
        BEGIN
            UPDATE {tableName}
            SET NotesLastUpdatedAt = GETDATE()
            WHERE ConversationID = {{3}};
        END
        ELSE
        BEGIN
            UPDATE {tableName}
            SET NotesLastUpdatedAt = GETDATE()
            WHERE ({{2}} > 0 AND AgentId = {{2}})
               OR ({{2}} <= 0 AND (
                    LOWER(LTRIM(RTRIM(ISNULL(AgentName, '')))) = LOWER(LTRIM(RTRIM({{1}})))
                    OR LOWER(ISNULL(AgentName, '')) LIKE '%' + LOWER(LTRIM(RTRIM({{1}}))) + '%'
               ));
        END
    END
END";
            await _context.Database.ExecuteSqlRawAsync(sql, notes ?? string.Empty, agentName ?? string.Empty, userId, conversationId);
        }

        private async Task UpdateAgentNotesAsync(int userId, string agentName, int conversationId, int? chatSlot, string notes)
        {
            var normalizedSlot = (chatSlot.HasValue && chatSlot.Value >= 1 && chatSlot.Value <= 3)
                ? chatSlot.Value
                : (int?)null;

            var agentSql = @"
IF OBJECT_ID('dbo.Agents', 'U') IS NOT NULL
BEGIN
    IF COL_LENGTH('dbo.Agents', 'Notes') IS NOT NULL
    BEGIN
        UPDATE dbo.Agents
        SET Notes = {0}
        WHERE ConversationID = {1}
          AND ({4} = 0 OR ChatSlot = {4});
    END

    IF COL_LENGTH('dbo.Agents', 'NotesLastUpdatedAt') IS NOT NULL
    BEGIN
        UPDATE dbo.Agents
        SET NotesLastUpdatedAt = GETDATE()
        WHERE ConversationID = {1}
          AND ({4} = 0 OR ChatSlot = {4});
    END
END";

            await _context.Database.ExecuteSqlRawAsync(
                agentSql,
                notes ?? string.Empty,
                conversationId,
                agentName ?? string.Empty,
                userId,
                normalizedSlot ?? 0);

            if (normalizedSlot.HasValue)
                await UpdateChatSlotNotesAsync(normalizedSlot.Value, agentName ?? string.Empty, userId, conversationId, notes ?? string.Empty);
        }

        private async Task UpdateAcwTrackingAsync(int conversationId, int? chatSlot, string agentName, int userId, bool isAcwStart)
        {
            var normalizedSlot = (chatSlot.HasValue && chatSlot.Value >= 1 && chatSlot.Value <= 3)
                ? chatSlot.Value
                : 0;

            var agentSql = @"
IF OBJECT_ID('dbo.Agents', 'U') IS NOT NULL
BEGIN
    IF {2} = 1
    BEGIN
        IF COL_LENGTH('dbo.Agents', 'ACWStartTime') IS NOT NULL
        BEGIN
            UPDATE dbo.Agents
            SET ACWStartTime = GETDATE()
            WHERE ConversationID = {0}
              AND ({1} = 0 OR ChatSlot = {1});
        END

        IF COL_LENGTH('dbo.Agents', 'ACWEndTime') IS NOT NULL
        BEGIN
            UPDATE dbo.Agents
            SET ACWEndTime = NULL
            WHERE ConversationID = {0}
              AND ({1} = 0 OR ChatSlot = {1});
        END
    END
    ELSE
    BEGIN
        IF COL_LENGTH('dbo.Agents', 'ACWEndTime') IS NOT NULL
        BEGIN
            UPDATE dbo.Agents
            SET ACWEndTime = GETDATE()
            WHERE ConversationID = {0}
              AND ({1} = 0 OR ChatSlot = {1})
              AND (ACWEndTime IS NULL);
        END
    END
END";

            await _context.Database.ExecuteSqlRawAsync(agentSql, conversationId, normalizedSlot, isAcwStart ? 1 : 0);
        }

        private async Task UpdateChatSlotAcwTimesAsync(int slot, int conversationId, string agentName, int userId, bool isAcwStart)
        {
            if (slot < 1 || slot > 3) return;

            var tableName = $"dbo.ChatSlot_{slot}";
            var sql = $@"
IF OBJECT_ID('{tableName}', 'U') IS NOT NULL
BEGIN
    IF {{4}} = 1
    BEGIN
        IF COL_LENGTH('{tableName}', 'ACWStartTime') IS NOT NULL
        BEGIN
            IF COL_LENGTH('{tableName}', 'ConversationID') IS NOT NULL
            BEGIN
                UPDATE {tableName}
                SET ACWStartTime = GETDATE()
                WHERE ConversationID = {{3}};
            END
            ELSE
            BEGIN
                UPDATE {tableName}
                SET ACWStartTime = GETDATE()
                WHERE ({{2}} > 0 AND AgentId = {{2}})
                   OR ({{2}} <= 0 AND (
                        LOWER(LTRIM(RTRIM(ISNULL(AgentName, '')))) = LOWER(LTRIM(RTRIM({{1}})))
                        OR LOWER(ISNULL(AgentName, '')) LIKE '%' + LOWER(LTRIM(RTRIM({{1}}))) + '%'
                   ));
            END
        END

        IF COL_LENGTH('{tableName}', 'ACWEndTime') IS NOT NULL
        BEGIN
            IF COL_LENGTH('{tableName}', 'ConversationID') IS NOT NULL
            BEGIN
                UPDATE {tableName}
                SET ACWEndTime = NULL
                WHERE ConversationID = {{3}};
            END
            ELSE
            BEGIN
                UPDATE {tableName}
                SET ACWEndTime = NULL
                WHERE ({{2}} > 0 AND AgentId = {{2}})
                   OR ({{2}} <= 0 AND (
                        LOWER(LTRIM(RTRIM(ISNULL(AgentName, '')))) = LOWER(LTRIM(RTRIM({{1}})))
                        OR LOWER(ISNULL(AgentName, '')) LIKE '%' + LOWER(LTRIM(RTRIM({{1}}))) + '%'
                   ));
            END
        END
    END
    ELSE
    BEGIN
        IF COL_LENGTH('{tableName}', 'ACWEndTime') IS NOT NULL
        BEGIN
            IF COL_LENGTH('{tableName}', 'ConversationID') IS NOT NULL
            BEGIN
                UPDATE {tableName}
                SET ACWEndTime = CASE
                    WHEN COL_LENGTH('{tableName}', 'ACWStartTime') IS NOT NULL
                         AND ACWStartTime IS NOT NULL
                         AND DATEADD(MINUTE, 2, ACWStartTime) < GETDATE()
                        THEN DATEADD(MINUTE, 2, ACWStartTime)
                    ELSE GETDATE()
                END
                WHERE ConversationID = {{3}}
                  AND (COL_LENGTH('{tableName}', 'ACWStartTime') IS NULL OR ACWStartTime IS NOT NULL)
                  AND (ACWEndTime IS NULL OR (COL_LENGTH('{tableName}', 'ACWStartTime') IS NOT NULL AND ACWEndTime < ACWStartTime));
            END
            ELSE
            BEGIN
                UPDATE {tableName}
                SET ACWEndTime = CASE
                    WHEN COL_LENGTH('{tableName}', 'ACWStartTime') IS NOT NULL
                         AND ACWStartTime IS NOT NULL
                         AND DATEADD(MINUTE, 2, ACWStartTime) < GETDATE()
                        THEN DATEADD(MINUTE, 2, ACWStartTime)
                    ELSE GETDATE()
                END
                WHERE (({{2}} > 0 AND AgentId = {{2}})
                   OR ({{2}} <= 0 AND (
                        LOWER(LTRIM(RTRIM(ISNULL(AgentName, '')))) = LOWER(LTRIM(RTRIM({{1}})))
                        OR LOWER(ISNULL(AgentName, '')) LIKE '%' + LOWER(LTRIM(RTRIM({{1}}))) + '%'
                   )))
                  AND (COL_LENGTH('{tableName}', 'ACWStartTime') IS NULL OR ACWStartTime IS NOT NULL)
                  AND (ACWEndTime IS NULL OR (COL_LENGTH('{tableName}', 'ACWStartTime') IS NOT NULL AND ACWEndTime < ACWStartTime));
            END
        END
    END
END";

            await _context.Database.ExecuteSqlRawAsync(sql, slot, agentName ?? string.Empty, userId, conversationId, isAcwStart ? 1 : 0);
        }

        private async Task<int> ResolveAgentUserIdAsync(int userId, string agentName)
        {
            if (userId > 0) return userId;
            if (!IsUsableAgentName(agentName)) return 0;

            var candidate = await _context.Agents
                .Where(a => a.AgentName == agentName && a.AgentID.HasValue)
                .OrderByDescending(a => a.ChatID)
                .Select(a => a.AgentID)
                .FirstOrDefaultAsync();

            return candidate ?? 0;
        }

        private async Task UpsertAgentStatusRowsAsync(int userId, string agentName, string agentStatus)
        {
            var persistedAgentName = ResolvePersistedAgentName(agentName, userId);
            var hasUsableAgentName = IsUsableAgentName(persistedAgentName);

            var agentRows = await _context.Agents
                .Where(a => (userId != 0 && a.AgentID == userId)
                    || (hasUsableAgentName && a.AgentName == persistedAgentName))
                .ToListAsync();

            if (agentRows.Count > 0)
            {
                foreach (var row in agentRows)
                    row.AgentStatus = agentStatus;

                await _context.SaveChangesAsync();
                return;
            }

            var latestAgent = await _context.Agents
                .Where(a => (userId != 0 && a.AgentID == userId)
                    || (hasUsableAgentName && a.AgentName == persistedAgentName))
                .OrderByDescending(a => a.ChatID)
                .FirstOrDefaultAsync();

            var newRow = new Agent
            {
                AgentID = userId > 0 ? userId : null,
                AgentName = hasUsableAgentName ? persistedAgentName : $"Agent {userId}",
                ClientName = latestAgent?.ClientName ?? "N/A",
                Category = latestAgent?.Category ?? "N/A",
                PreviewQuestion = latestAgent?.PreviewQuestion ?? "Status update",
                ChatSlot = latestAgent?.ChatSlot ?? 1,
                ChatStatus = latestAgent?.ChatStatus ?? "Available",
                AgentStatus = agentStatus
            };

            _context.Agents.Add(newRow);
            await _context.SaveChangesAsync();
        }

        private string ResolvePersistedAgentName(string requestedAgentName, int userId)
        {
            if (IsUsableAgentName(requestedAgentName)) return requestedAgentName.Trim();
            var sessionFullName = HttpContext.Session.GetString("FullName") ?? string.Empty;
            if (IsUsableAgentName(sessionFullName)) return sessionFullName.Trim();
            var username = HttpContext.Session.GetString("Username") ?? string.Empty;
            if (IsUsableAgentName(username)) return username.Trim();
            return userId > 0 ? $"Agent {userId}" : "Agent";
        }
    }

    public class UpdateAgentStatusRequest
    {
        public string AgentName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public int ConversationId { get; set; }
        public bool IsChatStatus { get; set; }
        public int? ChatSlot { get; set; }
        public int AgentUserId { get; set; }
    }

    public class SaveConversationNotesRequest
    {
        public int ConversationId { get; set; }
        public string AgentName { get; set; } = string.Empty;
        public int? ChatSlot { get; set; }
        public string Notes { get; set; } = string.Empty;
        public int AgentUserId { get; set; }
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