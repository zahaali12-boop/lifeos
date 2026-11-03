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
# The edition a workspace made from this build opens in (full, or trader for the trader program's own stack).
# The API is reached on the app's own origin: nginx below proxies /api and /health to the api container.
ARG VITE_EDITION=full
ENV VITE_EDITION=$VITE_EDITION
RUN pnpm --filter @quicker/web build

FROM nginx:alpine AS static
COPY deploy/docker/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /workspace/apps/web/dist /usr/share/nginx/html
EXPOSE 80
