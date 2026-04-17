using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NextHorizon.Models
{
    [Table("Agents")]
    public class Agent
    {
        [Key]
        public int ChatID { get; set; }
        public int? ConversationID { get; set; }
        public int? AgentID { get; set; }

        [Required]
        [MaxLength(100)]
        public string AgentName { get; set; } = string.Empty;

        [Required]
        [MaxLength(100)]
        public string ClientName { get; set; } = string.Empty;

        [Required]
        [MaxLength(100)]
        public string Category { get; set; } = string.Empty;

        [Required]
        [MaxLength(500)]
        public string PreviewQuestion { get; set; } = string.Empty;

        [Required]
        public int ChatSlot { get; set; }

        [Required]
        [MaxLength(20)]
        public string ChatStatus { get; set; } = "Active";

        [Required]
        [MaxLength(20)]
        public string AgentStatus { get; set; } = "Online";

        // Optional fields used by AgentController for ACW tracking and notes
        public DateTime? ACWStartTime { get; set; }
        public DateTime? ACWEndTime { get; set; }

        [MaxLength(2000)]
        public string? Notes { get; set; }
        public DateTime? NotesLastUpdatedAt { get; set; }
    }
}