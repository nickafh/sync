'use client';

import { useState, type FormEvent } from 'react';
import { useRouter } from 'next/navigation';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Card, CardContent } from '@/components/ui/card';
import { api } from '@/lib/api';

export function LoginForm() {
  const router = useRouter();
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');
  const [isSubmitting, setIsSubmitting] = useState(false);

  async function handleSubmit(e: FormEvent) {
    e.preventDefault();
    setError('');
    setIsSubmitting(true);

    try {
      await api.login(username, password);
      router.push('/');
    } catch {
      setError('Unable to sign in. Please check your credentials and try again.');
    } finally {
      setIsSubmitting(false);
    }
  }

  return (
    <Card className="w-full max-w-[400px] border-border-default shadow-sm">
      <CardContent className="p-8">
        <h1 className="font-heading text-[2rem] font-bold leading-[1.1] text-center mb-1">
          AFH Sync
        </h1>
        <p className="text-sm text-text-muted text-center mb-8">
          Contact sync management
        </p>

        <form onSubmit={handleSubmit} className="space-y-4">
          <div className="space-y-2">
            <Label htmlFor="username" className="text-[0.8125rem]">
              Username
            </Label>
            <Input
              id="username"
              type="text"
              placeholder="Enter your username"
              value={username}
              onChange={(e) => setUsername(e.target.value)}
              required
              disabled={isSubmitting}
              className="bg-white border-border-default focus-visible:ring-gold"
            />
          </div>

          <div className="space-y-2">
            <Label htmlFor="password" className="text-[0.8125rem]">
              Password
            </Label>
            <Input
              id="password"
              type="password"
              placeholder="Enter your password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              required
              disabled={isSubmitting}
              className="bg-white border-border-default focus-visible:ring-gold"
            />
          </div>

          <Button
            type="submit"
            disabled={isSubmitting}
            className="w-full min-h-[44px] bg-navy text-white hover:bg-navy-hover disabled:opacity-70 disabled:cursor-not-allowed"
          >
            {isSubmitting ? 'Signing in...' : 'Sign in'}
          </Button>

          {error && (
            <p role="alert" className="text-sm text-text-destructive text-center">
              {error}
            </p>
          )}
        </form>
      </CardContent>
    </Card>
  );
}
