FROM node:22-alpine AS base
RUN corepack enable
WORKDIR /workspace
COPY package.json pnpm-workspace.yaml pnpm-lock.yaml ./
COPY apps ./apps
COPY packages ./packages
COPY tools ./tools
RUN pnpm install --frozen-lockfile

FROM base AS dev
EXPOSE 5173
CMD ["pnpm", "--filter", "@quicker/web", "dev", "--host", "0.0.0.0"]

FROM base AS build
RUN pnpm --filter @quicker/web build

FROM nginx:alpine AS static
COPY --from=build /workspace/apps/web/dist /usr/share/nginx/html
EXPOSE 80
