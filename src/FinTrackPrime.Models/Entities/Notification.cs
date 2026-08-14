using System;

namespace FinTrackPrime.Models.Entities
{
    public class Notification
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public User? User { get; set; }

        public NotificationType Type { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;

        // Set for UnusualSpend, null for any notification type not tied
        // to one specific transaction. A filtered unique index on this
        // column (see FinTrackDbContext) guarantees a transaction never
        // gets flagged twice, even across separate SpendMonitorService
        // runs.
        public Guid? RelatedTransactionId { get; set; }

        public bool IsRead { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }
}
