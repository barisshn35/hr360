// İlk açılış: sunucu adresi sorulur, yerel olarak kaydedilir ve uygulama o adresi açar.
// Yerel köprü (HR360Native, MainActivity) adresi saklar; sonraki açılışlarda doğrudan sunucu açılır.
(function () {
  var form = document.getElementById('f');
  var input = document.getElementById('url');
  var err = document.getElementById('err');
  var params = new URLSearchParams(location.search);
  var last = (window.HR360Native && HR360Native.getServer()) || '';
  if (last) input.value = last;
  if (params.get('hata')) show('Sunucuya ulaşılamadı. Adresi ve internet bağlantınızı denetleyin.');

  function show(msg) { err.textContent = msg; err.hidden = false; }

  /** "ik.sirket.com" → "https://ik.sirket.com"; yalnızca https, yol/sorgu atılır. */
  function normalize(raw) {
    var v = (raw || '').trim();
    if (!v) return null;
    if (!/^[a-z]+:\/\//i.test(v)) v = 'https://' + v;
    try {
      var u = new URL(v);
      if (u.protocol !== 'https:' || !u.hostname || u.username || u.password) return null;
      return u.origin;
    } catch (e) { return null; }
  }

  form.addEventListener('submit', function (e) {
    e.preventDefault();
    var origin = normalize(input.value);
    if (!origin) { show('Geçerli bir https adresi girin (ör. https://ik.sirketiniz.com).'); return; }
    if (!window.HR360Native) { show('Uygulama köprüsü bulunamadı.'); return; }
    HR360Native.setServer(origin);
  });
})();
