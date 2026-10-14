ARG NODE_IMAGE=node:24-bookworm-slim
FROM ${NODE_IMAGE} AS dependencies
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --omit=dev --ignore-scripts --no-audit --no-fund --engine-strict

FROM ${NODE_IMAGE}
ENV NODE_ENV=production PORT=3000 DATA_PATH=/data/reporting.sqlite
WORKDIR /app
COPY --from=dependencies --chown=10001:10001 /app/node_modules ./node_modules
COPY --chown=10001:10001 package.json package-lock.json ./
COPY --chown=10001:10001 src ./src
COPY --chown=10001:10001 public ./public
COPY --chown=10001:10001 scripts ./scripts
RUN mkdir /data && chown 10001:10001 /data && chmod 700 /data
USER 10001:10001
EXPOSE 3000
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 CMD ["node","-e","fetch('http://127.0.0.1:3000/readyz').then(r=>process.exit(r.ok?0:1)).catch(()=>process.exit(1))"]
CMD ["node","src/server.mjs"]
