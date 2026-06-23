FROM node:22-alpine AS deps
WORKDIR /app
COPY package.json package-lock.json* ./
# Use `npm install` (not `npm ci`): the lockfile is generated on macOS and can't fully
# describe linux-only optional deps (rollup/vitest native bindings, @emnapi/*), which makes
# the stricter `npm ci` fail here. `npm install` resolves the correct deps for this platform.
RUN npm install --no-audit --no-fund

FROM node:22-alpine AS builder
WORKDIR /app
COPY --from=deps /app/node_modules ./node_modules
COPY . .
RUN npm run build

FROM node:22-alpine AS runner
WORKDIR /app
ENV NODE_ENV=production
RUN addgroup --system --gid 1001 nodejs
RUN adduser --system --uid 1001 nextjs
COPY --from=builder /app/public ./public
COPY --from=builder --chown=nextjs:nodejs /app/.next/standalone ./
COPY --from=builder --chown=nextjs:nodejs /app/.next/static ./.next/static
USER nextjs
EXPOSE 3000
CMD ["node", "server.js"]
