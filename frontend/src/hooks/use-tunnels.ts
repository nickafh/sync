'use client';

import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { api } from '@/lib/api';
import type { UpdateTunnelRequest, CreateTunnelRequest } from '@/types/tunnel';

export function useTunnels() {
  return useQuery({
    queryKey: ['tunnels'],
    queryFn: () => api.tunnels.list(),
    staleTime: 60 * 1000,
  });
}

export function useTunnel(id: number) {
  return useQuery({
    queryKey: ['tunnel', id],
    queryFn: () => api.tunnels.get(id),
    staleTime: 60 * 1000,
    enabled: id > 0,
  });
}

export function useUpdateTunnel() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: ({ id, data }: { id: number; data: UpdateTunnelRequest }) =>
      api.tunnels.update(id, data),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({ queryKey: ['tunnels'] });
      queryClient.invalidateQueries({ queryKey: ['tunnel', variables.id] });
      queryClient.invalidateQueries({ queryKey: ['dashboard'] });
    },
  });
}

export function useUpdateTunnelStatus() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: ({ id, status }: { id: number; status: string }) =>
      api.tunnels.updateStatus(id, status),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({ queryKey: ['tunnels'] });
      queryClient.invalidateQueries({ queryKey: ['tunnel', variables.id] });
      queryClient.invalidateQueries({ queryKey: ['dashboard'] });
    },
  });
}

export function useDeleteTunnel() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (id: number) => api.tunnels.delete(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['tunnels'] });
      queryClient.invalidateQueries({ queryKey: ['dashboard'] });
    },
  });
}

export function useCreateTunnel() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (data: CreateTunnelRequest) => api.tunnels.create(data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['tunnels'] });
      queryClient.invalidateQueries({ queryKey: ['dashboard'] });
    },
  });
}

export function usePreviewTunnelImpact() {
  return useMutation({
    mutationFn: ({ id, data }: { id: number; data: UpdateTunnelRequest }) =>
      api.tunnels.preview(id, data),
  });
}

export function useRefreshDdg() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ tunnelId, sourceId }: { tunnelId: number; sourceId: number }) =>
      api.tunnels.refreshDdg(tunnelId, sourceId),
    onSuccess: (_data, { tunnelId }) => {
      queryClient.invalidateQueries({ queryKey: ['tunnel', tunnelId] });
      queryClient.invalidateQueries({ queryKey: ['tunnels'] });
    },
  });
}
