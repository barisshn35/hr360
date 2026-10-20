# --- Aşama 1: build ---
FROM node:22-alpine AS build
WORKDIR /app

COPY package.json package-lock.json ./
RUN npm ci

COPY . .
# Uygulama surumu (depo kokundeki VERSION); docker-compose.yml build argumani olarak verir.
ARG HR360_VERSION=
ENV HR360_VERSION=${HR360_VERSION}
# Prod'da API ve Keycloak aynı origin'den (gateway) servis edilir.
ENV VITE_KEYCLOAK_URL=/auth \
    VITE_KEYCLOAK_REALM=hr360 \
    VITE_KEYCLOAK_CLIENT_ID=hr360-web \
    VITE_API_BASE=
RUN npm run build \
 # Önceden sıkıştırılmış kopyalar (nginx gzip_static): en yüksek oran, istek başına işlemci harcanmaz.
 && find dist -type f \( -name '*.js' -o -name '*.css' -o -name '*.json' -o -name '*.svg' -o -name '*.html' -o -name '*.webmanifest' \) \
      -size +1k -exec gzip -9 -k -n {} +

# --- Aşama 2: serve ---
# Root olmayan nginx (UID 101, 8080'i dinler).
FROM nginxinc/nginx-unprivileged:stable-alpine
COPY --from=build /app/dist /usr/share/nginx/html
COPY docker/nginx-spa.conf /etc/nginx/conf.d/default.conf
COPY docker/security-headers.conf /etc/nginx/snippets/security-headers.conf
# Taban imajdaki Alpine paketlerinin guvenlik yamalari.
USER root
RUN apk upgrade --no-cache
USER 101

EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s \
  CMD wget -qO- http://127.0.0.1:8080/healthz || exit 1

CMD ["nginx", "-g", "daemon off;"]
