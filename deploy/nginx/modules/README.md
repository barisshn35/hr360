# Kapalı modül işaretleri

`scripts/modules.sh disable <modül>` bu klasöre `<modül>.off` yazar; ağ geçidi o modülün `/api/...` ve
`/ml/...` isteklerine `404 {"code":"module_not_installed"}` döner. Dosyalar git'e girmez ve elle
düzenlenmez; nginx'in yeniden yüklenmesi gerekmez.
