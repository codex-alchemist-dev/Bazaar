(function () {
  'use strict';
  var button = document.getElementById('passkey');
  var message = document.getElementById('message');
  var audience = new URLSearchParams(location.search).get('audience') || 'native';
  var failure = 'Sign-in failed.';

  function fromB64(s) {
    s = s.replace(/-/g, '+').replace(/_/g, '/');
    while (s.length % 4) { s += '='; }
    var bin = atob(s);
    var out = new Uint8Array(bin.length);
    for (var i = 0; i < bin.length; i++) { out[i] = bin.charCodeAt(i); }
    return out.buffer;
  }

  function toB64(buf) {
    var bytes = new Uint8Array(buf);
    var bin = '';
    for (var i = 0; i < bytes.length; i++) { bin += String.fromCharCode(bytes[i]); }
    return btoa(bin).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  }

  function post(path, body) {
    return fetch(path, {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body || {})
    });
  }

  async function signIn() {
    button.disabled = true;
    message.textContent = '';
    try {
      var res = await post('/signin/challenge');
      if (!res.ok) { throw new Error(failure); }
      var c = await res.json();
      var options = { challenge: fromB64(c.challenge), timeout: c.timeoutMs, userVerification: 'required' };
      if (c.rpId) { options.rpId = c.rpId; }
      var cred = await navigator.credentials.get({ publicKey: options });
      var r = cred.response;
      var assertion = JSON.stringify({
        id: cred.id,
        rawId: toB64(cred.rawId),
        type: cred.type,
        response: {
          clientDataJSON: toB64(r.clientDataJSON),
          authenticatorData: toB64(r.authenticatorData),
          signature: toB64(r.signature),
          userHandle: r.userHandle ? toB64(r.userHandle) : null
        }
      });
      var done = await post('/signin/assert?audience=' + encodeURIComponent(audience), { challengeId: c.id, assertion: assertion, audience: audience });
      if (!done.ok) { throw new Error(failure); }
      var body = await done.json();
      location.assign(body.redirect);
    } catch (e) {
      message.textContent = failure;
      button.disabled = false;
    }
  }

  button.addEventListener('click', signIn);
})();
