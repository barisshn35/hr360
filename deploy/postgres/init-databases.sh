#!/bin/sh
# Postgres container ilk kez ayaga kalkarken calisir (docker-entrypoint-initdb.d).
# POSTGRES_DB=hr360_operational zaten otomatik olusuyor; burada MLflow
# (hr360_mlflow) ve Keycloak (keycloak) veritabanlari eklenir.
set -e

# NOT: --dbname VERILMEZSE psql varsayilan olarak kullanici adiyla AYNI
# isimde bir veritabanina baglanmaya calisir (burada "hr360admin") - o
# veritabani hic var olmadigindan baglanti FATAL hatasiyla basarisiz olur
# ve CREATE DATABASE komutu hic calismadan script sessizce atlanir (mlflow
# container'i sonradan "hr360_mlflow veritabani yok" hatasiyla patlar).
# Her zaman var olan "postgres" admin veritabanina baglanip oradan yeni DB
# yaratmak dogru/standart yontem.
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres <<-EOSQL
    CREATE DATABASE hr360_mlflow OWNER $POSTGRES_USER;
    -- Keycloak kalici veritabani (onceden Keycloak konteyner icindeki dosya
    -- veritabaninda calisiyordu; konteyner yeniden olusunca tum kullanicilar
    -- ve sirket organizasyonlari siliniyordu).
    CREATE DATABASE keycloak OWNER $POSTGRES_USER;
EOSQL
