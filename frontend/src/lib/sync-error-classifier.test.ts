import { describe, it, expect } from 'vitest';
import { classifySyncError, groupSyncErrors } from './sync-error-classifier';

describe('classifySyncError', () => {
  it('classifies an inactive/soft-deleted/on-premise mailbox as auto-skipped (info)', () => {
    const c = classifySyncError(
      "Folder 'Buckhead': The mailbox is either inactive, soft-deleted, or is hosted on-premise.",
    );
    expect(c.category).toBe('mailbox-removed');
    expect(c.severity).toBe('info');
    expect(c.title).toBe('Mailbox no longer active');
    expect(c.guidance).toMatch(/automatically/i);
    expect(c.raw).toContain('inactive');
  });

  it('classifies an invalid requested user as user-not-found (info)', () => {
    const c = classifySyncError("The requested user 'bb09...' is invalid.");
    expect(c.category).toBe('user-invalid');
    expect(c.severity).toBe('info');
    expect(c.title).toBe('User account not found');
  });

  it('classifies an EF duplicate-key / save error as an internal error (red)', () => {
    const c = classifySyncError(
      'Blue Ridge: An error occurred while saving the entity changes. See the inner exception for details.',
    );
    expect(c.category).toBe('internal');
    expect(c.severity).toBe('error');
    expect(c.title).toBe('Internal sync error');
  });

  it('classifies throttling as a rate limit (info)', () => {
    const c = classifySyncError('Request was throttled (429). Retry-After: 30');
    expect(c.category).toBe('rate-limit');
    expect(c.severity).toBe('info');
    expect(c.title).toBe('Microsoft rate limit');
  });

  it('falls back to unknown/error for unrecognized text, preserving the raw message', () => {
    const c = classifySyncError('Some brand new error nobody mapped yet');
    expect(c.category).toBe('unknown');
    expect(c.severity).toBe('error');
    expect(c.title).toBe('Unexpected error');
    expect(c.raw).toBe('Some brand new error nobody mapped yet');
  });
});

describe('groupSyncErrors', () => {
  it('collapses identical errors into one group with a count and raw samples', () => {
    const raw = Array.from(
      { length: 37 },
      () => "Folder 'Buckhead': The mailbox is either inactive, soft-deleted, or is hosted on-premise.",
    );
    const groups = groupSyncErrors(raw);
    expect(groups).toHaveLength(1);
    expect(groups[0].category).toBe('mailbox-removed');
    expect(groups[0].count).toBe(37);
    expect(groups[0].rawSamples.length).toBeGreaterThan(0);
  });

  it('separates auto-skipped (info) groups from real failures (error), sorted errors first', () => {
    const groups = groupSyncErrors([
      "Folder 'Buckhead': The mailbox is either inactive, soft-deleted, or is hosted on-premise.",
      "Folder 'Buckhead': The mailbox is either inactive, soft-deleted, or is hosted on-premise.",
      'An error occurred while saving the entity changes. See the inner exception for details.',
    ]);
    expect(groups).toHaveLength(2);
    // errors sort before info so the actionable item is on top
    expect(groups[0].severity).toBe('error');
    expect(groups[0].count).toBe(1);
    expect(groups[1].category).toBe('mailbox-removed');
    expect(groups[1].count).toBe(2);
  });
});
