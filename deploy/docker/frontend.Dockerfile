# syntax=docker/dockerfile:1
# Frontend image: Vite build -> nginx. Build context = repo root.
#   docker build -f deploy/docker/frontend.Dockerfile -t beko-frontend .

FROM --platform=$BUILDPLATFORM node:22.23.4-alpine AS build
WORKDIR /src/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

FROM nginx:1.30.6-alpine AS runtime
# nginx image renders /etc/nginx/templates/*.template with envsubst at start.
# BACKEND_UPSTREAM: host:port of the backend (Compose: backend:8080; k8s: backend.beko.svc.cluster.local:8080,
# because nginx's resolver does not use DNS search domains).
ENV BACKEND_UPSTREAM=backend:8080 \
    NGINX_ENTRYPOINT_LOCAL_RESOLVERS=1 \
    NGINX_ENVSUBST_FILTER=^(BACKEND_|NGINX_)
COPY deploy/docker/nginx.conf /etc/nginx/templates/default.conf.template
COPY --from=build /src/frontend/dist /usr/share/nginx/html
# nginx master runs as root only to bind :80; workers run as the nginx user.
EXPOSE 80
