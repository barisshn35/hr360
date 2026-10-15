FROM python:3.14-slim
WORKDIR /app
# Taban imajdaki isletim sistemi ve pip araclarinin guvenlik yamalari.
# tesseract: fiş okuma (yerel OCR, Türkçe + İngilizce dil verisi).
RUN apt-get update && apt-get upgrade -y --no-install-recommends \
 && apt-get install -y --no-install-recommends tesseract-ocr tesseract-ocr-tur tesseract-ocr-eng \
 && rm -rf /var/lib/apt/lists/* \
 && pip install --no-cache-dir --upgrade pip setuptools wheel
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
# AI araclarinin ek bagimliliklari ayri katmanda: agir ana katman (mlflow, shap)
# bunlar degisince yeniden kurulmasin.
COPY requirements-ai.txt .
RUN pip install --no-cache-dir -r requirements-ai.txt
# Tum Python modullerini al - performance_ml.py gibi ek router'lar
# eklendiginde Dockerfile'i tekrar duzeltmek gerekmesin.
COPY *.py ./
# Kural dosyalari (ör. pii_rules.json: kisisel veri dedektörünün anahtar sözcükleri).
COPY *.json ./
# Root olmayan kullanici; MLflow model indirmeleri /tmp ve ev dizinine yazar.
RUN useradd --create-home --uid 10001 app
USER 10001
CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8000"]
