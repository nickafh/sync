import { describe, it, expect } from 'vitest';
import { classifySyncError, groupSyncErrors, isAutoSkipped } from './sync-error-classifier';

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

  it('classifies a deleted-at-mailbox photo failure as auto-handled contact-recreated (info)', () => {
    const c = classifySyncError(
      'The specified object was not found in the store., The process failed to get the correct properties.',
    );
    expect(c.category).toBe('contact-recreated');
    expect(c.severity).toBe('info');
    expect(c.title).toBe('Contact was removed at the mailbox');
    expect(c.guidance).toMatch(/no action needed/i);
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

  it('classifies a Graph 5xx (gateway timeout etc.) as transient — retried next run (info)', () => {
    for (const raw of ['HTTP 504', 'HTTP 503', 'HTTP 502', 'Gateway Timeout while writing contact']) {
      const c = classifySyncError(raw);
      expect(c.category, raw).toBe('transient-graph');
      expect(c.severity, raw).toBe('info');
      expect(c.title, raw).toBe('Microsoft Graph timed out');
      expect(c.guidance, raw).toMatch(/next sync/i);
    }
  });

  it('does not treat a 4xx (other than 429) as transient', () => {
    expect(classifySyncError('HTTP 404').category).toBe('unknown');
    expect(classifySyncError('HTTP 400').category).toBe('unknown');
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

describe('isAutoSkipped', () => {
  it('counts transient Graph errors as auto-handled, like rate limits', () => {
    expect(isAutoSkipped('transient-graph')).toBe(true);
    expect(isAutoSkipped('rate-limit')).toBe(true);
    expect(isAutoSkipped('unknown')).toBe(false);
  });
});
