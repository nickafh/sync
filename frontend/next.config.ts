import type { NextConfig } from 'next';

const nextConfig: NextConfig = {
  output: 'standalone',
  async rewrites() {
    // In dev, proxy /api calls to the .NET API running natively
    return process.env.NODE_ENV === 'development'
      ? [{ source: '/api/:path*', destination: 'http://localhost:8080/api/:path*' }]
      : [];
  },
};

export default nextConfig;
