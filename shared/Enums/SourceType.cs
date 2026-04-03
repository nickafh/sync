using NpgsqlTypes;

namespace AFHSync.Shared.Enums;

public enum SourceType
{
    [PgName("ddg")] Ddg,
    [PgName("mailbox_contacts")] MailboxContacts
}
