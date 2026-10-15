FROM ghcr.io/mlflow/mlflow:v3.17.0
# Taban imajdaki isletim sistemi ve pip araclarinin guvenlik yamalari.
RUN apt-get update && apt-get upgrade -y --no-install-recommends && rm -rf /var/lib/apt/lists/* \
 && pip install --no-cache-dir --upgrade pip setuptools wheel
RUN pip install --no-cache-dir "psycopg[binary]" psycopg2-binary boto3 scikit-learn pandas fairlearn \
 && useradd --create-home --uid 10001 mlflow
# Sunucu yerel diske yazmaz (meta veri PostgreSQL'de, artefaktlar MinIO'da).
USER 10001
