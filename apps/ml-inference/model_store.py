"""
MLflow model kaydi (Registry) islemleri - devir riski modeli.

- Yayindaki surum "champion" takma adiyla (alias) isaretlenir. Takma ad yoksa
  (eski kurulum) MODEL_STAGE ortam degiskenindeki surum kullanilir.
- Her egitim yeni bir MLflow calismasi (run) ve yeni bir model surumu uretir;
  terfi etmeyen surumler kayitta kalir ("hr360.promoted" = false etiketiyle).
- Model kartinin meta verisi ve egitim referans histogramlari calismaya
  "attrition_meta.json" olarak yazilir (ham egitim satirlari YAZILMAZ).
- Veri kaymasi raporlari ayri bir deneyde (hr360-attrition-drift) tutulur.

mlflow ice aktarimi tembeldir: birim testleri bu modulu sahte bir depoyla degistirir.
"""

from __future__ import annotations

import json
import os
import tempfile
from dataclasses import dataclass, field

CHAMPION = "champion"
CHALLENGER = "challenger"
# hr360.status etiketi: champion (yayinda) | challenger (onay bekleyen aday) | rejected
# (karsilastirmayi gecemedi ya da deneme egitimi) | retired (eskiden yayindaydi; geri alinabilir)
META_FILE = "attrition_meta.json"
DRIFT_EXPERIMENT = "hr360-attrition-drift"
TRAIN_EXPERIMENT = "hr360-attrition-training"


@dataclass
class LoadedModel:
    model: object
    version: str
    meta: dict = field(default_factory=dict)


class ModelNotFound(Exception):
    pass


class ModelLoadError(Exception):
    """Surum kayitta var ama model dosyasi bu ortamda acilamadi (ör. kutuphane surumu uyumsuz)."""


def _load_sklearn(uri: str, version: str):
    import mlflow.sklearn
    try:
        return mlflow.sklearn.load_model(uri)
    except Exception as e:  # noqa: BLE001 - nedeni (surum uyumsuzlugu / indirme hatasi) mesajda kalir
        raise ModelLoadError(f"v{version}: {type(e).__name__}: {e}"[:500]) from e


class MlflowModelStore:
    def __init__(self, name: str, fallback_version: str):
        self.name = name
        self.fallback_version = fallback_version

    @staticmethod
    def _client():
        from mlflow.tracking import MlflowClient
        return MlflowClient()

    def load_current(self) -> LoadedModel:
        from mlflow.exceptions import MlflowException

        client = self._client()
        try:
            client.get_registered_model(self.name)
        except MlflowException as e:
            if "RESOURCE_DOES_NOT_EXIST" in str(e):
                raise ModelNotFound(str(e)) from e
            raise
        try:
            mv = client.get_model_version_by_alias(self.name, CHAMPION)
        except MlflowException:
            try:
                mv = client.get_model_version(self.name, self.fallback_version)
            except MlflowException as e2:
                raise ModelNotFound(str(e2)) from e2
        model = _load_sklearn(f"models:/{self.name}/{mv.version}", str(mv.version))
        return LoadedModel(model=model, version=str(mv.version), meta=self._meta(mv.run_id))

    @staticmethod
    def _meta(run_id: str | None) -> dict:
        if not run_id:
            return {}
        import mlflow.artifacts
        try:
            return mlflow.artifacts.load_dict(f"runs:/{run_id}/{META_FILE}")
        except Exception:  # noqa: BLE001 - eski surumlerde meta yok; kart kismi doner
            return {}

    def register(self, model, meta: dict, metrics: dict, params: dict, promote: bool,
                 challenger: bool = False) -> str:
        import mlflow
        import mlflow.sklearn

        mlflow.set_experiment(TRAIN_EXPERIMENT)
        with mlflow.start_run(run_name=f"attrition-{meta.get('trained_at', '')}"):
            mlflow.log_params(params)
            mlflow.log_metrics({k: v for k, v in metrics.items() if isinstance(v, (int, float)) and v == v})
            mlflow.set_tags({"hr360.source": meta.get("training_source", ""), "hr360.promoted": str(promote).lower()})
            mlflow.log_dict(meta, META_FILE)
            import numpy
            import sklearn
            # MLflow 3 modeli skops ile saklar ve agac dugum tipini acikca guvenilir saymak ister
            # (dosyayi bu servis uretir; disaridan model yuklenmez).
            info = mlflow.sklearn.log_model(
                model, name="model", registered_model_name=self.name,
                skops_trusted_types=["sklearn.tree._tree.Tree"],
                pip_requirements=[f"scikit-learn=={sklearn.__version__}", f"numpy=={numpy.__version__}"],
            )
            version = str(info.registered_model_version)
        client = self._client()
        client.set_model_version_tag(self.name, version, "hr360.promoted", str(promote).lower())
        status = "champion" if promote else "challenger" if challenger else "rejected"
        promotion = meta.get("promotion") or {}
        for k, v in {"hr360.status": status, "hr360.trained_at": meta.get("trained_at") or "",
                     "hr360.source": meta.get("training_source") or "",
                     "hr360.compared_with": promotion.get("compared_with_version") or "",
                     "hr360.recommend": str(bool(promotion.get("promote"))).lower(),
                     "hr360.reason": (promotion.get("reason") or "")[:240]}.items():
            if v:  # bos etiket degeri yazilmaz
                client.set_model_version_tag(self.name, version, k, v)
        if promote:
            client.set_model_version_tag(self.name, version, "hr360.was_champion", "true")
            client.set_registered_model_alias(self.name, CHAMPION, version)
        if challenger:
            client.set_registered_model_alias(self.name, CHALLENGER, version)
        return version

    def load_version(self, version: str) -> LoadedModel:
        from mlflow.exceptions import MlflowException

        try:
            mv = self._client().get_model_version(self.name, str(version))
        except MlflowException as e:
            raise ModelNotFound(str(e)) from e
        model = _load_sklearn(f"models:/{self.name}/{mv.version}", str(mv.version))
        return LoadedModel(model=model, version=str(mv.version), meta=self._meta(mv.run_id))

    def set_champion(self, version: str, previous: str | None) -> None:
        """Takma adi tasir: yayindaki surum degisir. Onceki surum "retired" olur (geri alinabilir)."""
        client = self._client()
        client.set_registered_model_alias(self.name, CHAMPION, str(version))
        for k, v in {"hr360.status": "champion", "hr360.promoted": "true", "hr360.was_champion": "true"}.items():
            client.set_model_version_tag(self.name, str(version), k, v)
        if previous and previous != str(version):
            client.set_model_version_tag(self.name, previous, "hr360.status", "retired")
        try:
            if str(client.get_model_version_by_alias(self.name, CHALLENGER).version) == str(version):
                client.delete_registered_model_alias(self.name, CHALLENGER)
        except Exception:  # noqa: BLE001 - aday takma adi yoksa sorun degil
            pass

    def list_versions(self, limit: int = 15) -> list[dict]:
        """Son surumler: etiketler ve calisma metrikleri (model dosyasi indirilmez)."""
        client = self._client()
        versions = client.search_model_versions(f"name='{self.name}'", max_results=200)
        versions = sorted(versions, key=lambda v: int(v.version), reverse=True)[:limit]
        out = []
        for mv in versions:
            try:
                metrics = dict(client.get_run(mv.run_id).data.metrics) if mv.run_id else {}
            except Exception:  # noqa: BLE001
                metrics = {}
            out.append(_version_row(str(mv.version), dict(mv.tags or {}), metrics,
                                    getattr(mv, "creation_timestamp", None)))
        return out

    def log_drift(self, tenant: str, model_version: str | None, report: dict) -> None:
        import mlflow

        mlflow.set_experiment(DRIFT_EXPERIMENT)
        with mlflow.start_run(run_name=f"drift-{tenant}"):
            mlflow.set_tags({"hr360.tenant": tenant, "hr360.model_version": model_version or "",
                             "hr360.status": report.get("status", "")})
            mlflow.log_metrics({f"psi_{f['feature']}": f["psi"] for f in report.get("features", [])}
                               | {"max_psi": report.get("max_psi", 0.0), "rows": report.get("rows", 0)})
            mlflow.log_dict(report, "drift.json")

    def last_drift(self, tenant: str) -> dict | None:
        import mlflow
        import mlflow.artifacts

        exp = mlflow.get_experiment_by_name(DRIFT_EXPERIMENT)
        if exp is None:
            return None
        safe = tenant.replace("'", "")
        runs = self._client().search_runs([exp.experiment_id], filter_string=f"tags.`hr360.tenant` = '{safe}'",
                                          order_by=["attributes.start_time DESC"], max_results=1)
        if not runs:
            return None
        try:
            return mlflow.artifacts.load_dict(f"runs:/{runs[0].info.run_id}/drift.json")
        except Exception:  # noqa: BLE001
            return None


def _version_row(version: str, tags: dict, metrics: dict, created_ms: int | None) -> dict:
    from datetime import datetime, timezone

    status = tags.get("hr360.status") or ("champion" if tags.get("hr360.promoted") == "true" else "rejected")
    return {
        "version": version, "status": status,
        "was_champion": tags.get("hr360.was_champion") == "true" or status == "champion",
        "trained_at": tags.get("hr360.trained_at") or (
            datetime.fromtimestamp(created_ms / 1000, tz=timezone.utc).isoformat(timespec="seconds") if created_ms else None),
        "source": tags.get("hr360.source") or None,
        "compared_with_version": tags.get("hr360.compared_with") or None,
        "recommended": tags.get("hr360.recommend") == "true",
        "reason": tags.get("hr360.reason") or None,
        "metrics": {k: metrics[k] for k in ("auc", "accuracy", "brier", "ece", "positive_rate") if k in metrics},
    }


class LocalModelStore:
    """Dosya sistemi tabanli depo: MLflow olmadan (birim testi / gelistirme) ayni arayuz."""

    def __init__(self, root: str | None = None):
        self.root = root or tempfile.mkdtemp(prefix="hr360-models-")
        self.models: dict[str, tuple[object, dict]] = {}
        self.champion: str | None = None
        self.drifts: dict[str, dict] = {}
        self.promoted: dict[str, bool] = {}
        self.tags: dict[str, dict] = {}
        self.metrics: dict[str, dict] = {}

    def load_current(self) -> LoadedModel:
        if self.champion is None:
            raise ModelNotFound("no model")
        model, meta = self.models[self.champion]
        return LoadedModel(model=model, version=self.champion, meta=meta)

    def register(self, model, meta, metrics, params, promote, challenger=False) -> str:
        version = str(len(self.models) + 1)
        self.models[version] = (model, json.loads(json.dumps(meta)))
        self.promoted[version] = promote
        promotion = meta.get("promotion") or {}
        self.tags[version] = {"hr360.status": "champion" if promote else "challenger" if challenger else "rejected",
                              "hr360.trained_at": meta.get("trained_at") or "",
                              "hr360.source": meta.get("training_source") or "",
                              "hr360.compared_with": promotion.get("compared_with_version") or "",
                              "hr360.recommend": str(bool(promotion.get("promote"))).lower(),
                              "hr360.reason": promotion.get("reason") or "",
                              "hr360.was_champion": "true" if promote else "false"}
        self.metrics[version] = dict(metrics)
        if promote:
            self.champion = version
        return version

    def load_version(self, version: str) -> LoadedModel:
        if str(version) not in self.models:
            raise ModelNotFound(f"no version {version}")
        model, meta = self.models[str(version)]
        return LoadedModel(model=model, version=str(version), meta=meta)

    def set_champion(self, version: str, previous: str | None) -> None:
        self.champion = str(version)
        self.promoted[str(version)] = True
        self.tags[str(version)].update({"hr360.status": "champion", "hr360.was_champion": "true"})
        if previous and previous != str(version) and previous in self.tags:
            self.tags[previous]["hr360.status"] = "retired"

    def list_versions(self, limit: int = 15) -> list[dict]:
        return [_version_row(v, self.tags.get(v, {}), self.metrics.get(v, {}), None)
                for v in sorted(self.models, key=int, reverse=True)[:limit]]

    def log_drift(self, tenant, model_version, report) -> None:
        self.drifts[tenant] = report
        with open(os.path.join(self.root, f"drift-{tenant}.json"), "w") as f:
            json.dump(report, f)

    def last_drift(self, tenant):
        return self.drifts.get(tenant)
