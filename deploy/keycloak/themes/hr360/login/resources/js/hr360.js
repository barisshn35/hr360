// HR360: "Parolamı unuttum" bağlantısı HR360'ın kendi akışına gider (/parola-sifirla). Keycloak'ın
// kendi sıfırlama bağlantısı realm'de kapalıdır (resetPasswordAllowed=false); parola linkleri HR360'ta
// tek kullanımlık ve sürelidir, e-posta yoksa sayfa "İK yöneticinize başvurun" der.
(function () {
  function add() {
    var form = document.getElementById('kc-form-login');
    if (!form || document.getElementById('hr360-forgot')) return;
    var en = (document.documentElement.lang || '').toLowerCase().indexOf('en') === 0;
    var settings = form.querySelector('.login-pf-settings') || form;
    var wrap = document.createElement('div');
    wrap.className = 'hr360-forgot-wrap';
    var a = document.createElement('a');
    a.id = 'hr360-forgot';
    a.href = '/parola-sifirla' + (en ? '?lang=en' : '');
    a.textContent = en ? 'Forgot password?' : 'Parolamı unuttum';
    wrap.appendChild(a);
    settings.appendChild(wrap);
  }
  // Çerez bilgilendirmesi: yalnızca zorunlu çerezler (oturum, güvenlik, dil). Rıza değil bilgilendirme;
  // "Anladım" uygulamayla aynı yerel depolama anahtarına yazılır (aynı köken).
  var KEY = 'hr360.cookieNotice';
  function cookieNotice() {
    try { if (localStorage.getItem(KEY) === '1') return; } catch (e) { /* depolama kapalı: bant gösterilir */ }
    if (document.getElementById('hr360-cookie')) return;
    var en = (document.documentElement.lang || '').toLowerCase().indexOf('en') === 0;
    var box = document.createElement('aside');
    box.id = 'hr360-cookie';
    box.className = 'hr360-cookie';
    box.setAttribute('role', 'region');
    box.setAttribute('aria-label', en ? 'Cookie notice' : 'Çerez bilgilendirmesi');
    var icon = document.createElement('span');
    icon.className = 'hr360-cookie-icon';
    icon.setAttribute('aria-hidden', 'true');
    icon.innerHTML = '<svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2a10 10 0 1 0 10 10 4 4 0 0 1-5-5 4 4 0 0 1-5-5"/><path d="M8.5 8.5v.01"/><path d="M16 15.5v.01"/><path d="M12 12v.01"/><path d="M11 17v.01"/><path d="M7 14v.01"/></svg>';
    var body = document.createElement('div');
    var title = document.createElement('p');
    title.className = 'hr360-cookie-title';
    title.textContent = en ? 'Only essential cookies are used' : 'Yalnızca zorunlu çerezler kullanılır';
    var text = document.createElement('p');
    text.className = 'hr360-cookie-text';
    text.textContent = en
      ? 'For sign-in, security and remembering your preferences. No advertising or tracking cookies.'
      : 'Giriş, güvenlik ve tercihlerinizi hatırlamak için. Reklam ya da izleme çerezi yok.';
    var actions = document.createElement('div');
    actions.className = 'hr360-cookie-actions';
    var ok = document.createElement('button');
    ok.type = 'button';
    ok.textContent = en ? 'Got it' : 'Anladım';
    ok.addEventListener('click', function () {
      try { localStorage.setItem(KEY, '1'); } catch (e) { /* gizli pencere */ }
      box.classList.add('hr360-cookie-out');
      setTimeout(function () { if (box.parentNode) box.parentNode.removeChild(box); }, 300);
    });
    var more = document.createElement('a');
    more.href = '/gizlilik' + (en ? '?lang=en' : '') + '#cerezler';
    more.textContent = en ? 'Details' : 'Ayrıntılar';
    actions.appendChild(ok);
    actions.appendChild(more);
    body.appendChild(title);
    body.appendChild(text);
    body.appendChild(actions);
    box.appendChild(icon);
    box.appendChild(body);
    document.body.appendChild(box);
  }

  function init() {
    add();
    cookieNotice();
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
  else init();
})();
