/**
 * Phase 3 (§3.2): one definition of the tunnel target scope the wizard and the edit page share.
 *
 * The form state encodes the scope in two nullable fields: `targetGroupId` (non-null ⇒ group
 * mode; '' is the "group mode, nothing picked yet" sentinel) and `targetUserEmails` (non-null
 * JSON array ⇒ specific-users mode; '[]' is "specific mode, nobody picked yet"). Deriving the
 * <Select> value from *presence* (not truthiness) is what keeps the dropdown on "Security Group"
 * while the user is still picking a group. Validation mirrors the API's TargetScopeValidation.
 */
export type TargetScopeOption = 'all' | 'specific' | 'group';

export const TARGET_SCOPE_MESSAGES = {
  emptyUsers: 'Select at least one user, or switch scope to All Users.',
  emptyGroup: 'Select a security group, or switch scope to All Users.',
} as const;

export function deriveTargetScope(
  targetGroupId: string | null | undefined,
  targetUserEmails: string | null | undefined,
): TargetScopeOption {
  if (targetGroupId !== null && targetGroupId !== undefined) return 'group';
  if (targetUserEmails !== null && targetUserEmails !== undefined) return 'specific';
  return 'all';
}

export function parseTargetUserEmails(json: string | null | undefined): string[] {
  if (!json) return [];
  try {
    const parsed: unknown = JSON.parse(json);
    return Array.isArray(parsed)
      ? parsed.filter((e): e is string => typeof e === 'string' && e.trim() !== '')
      : [];
  } catch {
    return [];
  }
}

/** Returns the error to show, or null when the scope is saveable. */
export function validateTargetScope(
  targetGroupId: string | null | undefined,
  targetUserEmails: string | null | undefined,
): string | null {
  switch (deriveTargetScope(targetGroupId, targetUserEmails)) {
    case 'group':
      return (targetGroupId ?? '').trim() === '' ? TARGET_SCOPE_MESSAGES.emptyGroup : null;
    case 'specific':
      return parseTargetUserEmails(targetUserEmails).length === 0 ? TARGET_SCOPE_MESSAGES.emptyUsers : null;
    default:
      return null;
  }
}
