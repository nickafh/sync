import { describe, it, expect } from 'vitest';
import {
  deriveTargetScope,
  parseTargetUserEmails,
  validateTargetScope,
  TARGET_SCOPE_MESSAGES,
} from './target-scope';

describe('deriveTargetScope', () => {
  it('is "all" when neither scope is set', () => {
    expect(deriveTargetScope(null, null)).toBe('all');
    expect(deriveTargetScope(undefined, undefined)).toBe('all');
  });

  it('is "group" whenever a group id is present — including the "" picking sentinel', () => {
    expect(deriveTargetScope('', null)).toBe('group');
    expect(deriveTargetScope('abc', null)).toBe('group');
  });

  it('is "specific" whenever an emails JSON is present — including "[]"', () => {
    expect(deriveTargetScope(null, '[]')).toBe('specific');
    expect(deriveTargetScope(null, '["a@x.com"]')).toBe('specific');
  });
});

describe('parseTargetUserEmails', () => {
  it('returns the non-blank strings of a JSON array', () => {
    expect(parseTargetUserEmails('["a@x.com", "", "b@x.com"]')).toEqual(['a@x.com', 'b@x.com']);
  });

  it('returns [] for null, "[]", non-arrays and bad JSON', () => {
    expect(parseTargetUserEmails(null)).toEqual([]);
    expect(parseTargetUserEmails('[]')).toEqual([]);
    expect(parseTargetUserEmails('{"emails":["a@x.com"]}')).toEqual([]);
    expect(parseTargetUserEmails('nope')).toEqual([]);
  });
});

describe('validateTargetScope', () => {
  it('accepts All Users, a chosen group and a non-empty user list', () => {
    expect(validateTargetScope(null, null)).toBeNull();
    expect(validateTargetScope('group-1', null)).toBeNull();
    expect(validateTargetScope(null, '["a@x.com"]')).toBeNull();
  });

  it('rejects group mode with no group picked', () => {
    expect(validateTargetScope('', null)).toBe(TARGET_SCOPE_MESSAGES.emptyGroup);
  });

  it('rejects specific mode with no users', () => {
    expect(validateTargetScope(null, '[]')).toBe(TARGET_SCOPE_MESSAGES.emptyUsers);
    expect(validateTargetScope(null, '[""]')).toBe(TARGET_SCOPE_MESSAGES.emptyUsers);
  });
});
