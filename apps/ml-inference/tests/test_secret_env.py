"""Sirlarin dosyadan okunmasi (secret_env.py) birim testleri."""

import os
import sys

import pytest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import secret_env  # noqa: E402


def test_dosya_yalnizca_degisken_bossa_okunur():
    env = {"INTERNAL_SERVICE_TOKEN_FILE": "/s/t", "AWS_SECRET_ACCESS_KEY": "ortam", "AWS_SECRET_ACCESS_KEY_FILE": "/s/a"}
    files = {"/s/t": "jeton\n", "/s/a": "dosya"}
    assert secret_env.load(env, files.__getitem__) == ["INTERNAL_SERVICE_TOKEN"]
    assert env["INTERNAL_SERVICE_TOKEN"] == "jeton"
    assert env["AWS_SECRET_ACCESS_KEY"] == "ortam"


def test_varsayilan_ortam_degismez():
    env = {"INTERNAL_SERVICE_TOKEN": "x"}
    assert secret_env.load(env, lambda p: "y") == []
    assert env == {"INTERNAL_SERVICE_TOKEN": "x"}


def test_okunamayan_ya_da_bos_dosya_hata():
    def missing(path):
        raise FileNotFoundError(path)
    with pytest.raises(RuntimeError, match="INTERNAL_SERVICE_TOKEN_FILE"):
        secret_env.load({"INTERNAL_SERVICE_TOKEN_FILE": "/yok"}, missing)
    with pytest.raises(RuntimeError):
        secret_env.load({"INTERNAL_SERVICE_TOKEN_FILE": "/bos"}, lambda p: "\n")
