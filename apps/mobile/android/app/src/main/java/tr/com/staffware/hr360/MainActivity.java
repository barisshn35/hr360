package tr.com.staffware.hr360;

import android.content.ActivityNotFoundException;
import android.content.Intent;
import android.content.SharedPreferences;
import android.graphics.Bitmap;
import android.net.Uri;
import android.os.Bundle;
import android.util.Base64;
import android.webkit.JavascriptInterface;
import android.webkit.WebResourceError;
import android.webkit.WebResourceRequest;
import android.webkit.WebView;
import androidx.activity.OnBackPressedCallback;
import androidx.core.content.FileProvider;
import com.getcapacitor.Bridge;
import com.getcapacitor.BridgeActivity;
import com.getcapacitor.BridgeWebViewClient;
import java.io.File;
import java.io.FileOutputStream;

/**
 * HR360 Android kabuğu (yenilik haritası #6). Tek APK tüm kurulumlarda çalışır: ilk açılışta sunucu
 * adresi sorulur (www/index.html), sonra kurulumun kendi arayüzü açılır.
 *
 * Güvenlik: uygulama içinde yalnızca yerel bağlan ekranı (https://localhost) ve kaydedilen sunucu
 * açılır (Keycloak girişi aynı adrestedir); diğer bağlantılar dış tarayıcıda açılır. Yerel köprü
 * (HR360Native) yalnızca bu iki kaynaktan gelen sayfalara yanıt verir.
 */
public class MainActivity extends BridgeActivity {
    private static final String PREFS = "hr360";
    private static final String KEY_SERVER = "server";
    private static final String LOCAL = "https://localhost";
    private static final long MAX_FILE = 25L * 1024 * 1024;

    /** O an yüklenen sayfanın kaynağı (köprü çağrıları arka plan iş parçacığında gelir). */
    private volatile String currentOrigin = LOCAL;

    @Override
    public void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        WebView web = bridge.getWebView();
        web.addJavascriptInterface(new NativeBridge(), "HR360Native");
        bridge.setWebViewClient(new Client(bridge));
        String server = server();
        if (server != null) web.loadUrl(server + "/panel");

        getOnBackPressedDispatcher().addCallback(this, new OnBackPressedCallback(true) {
            @Override
            public void handleOnBackPressed() {
                if (web.canGoBack()) { web.goBack(); return; }
                setEnabled(false);
                getOnBackPressedDispatcher().onBackPressed();
                setEnabled(true);
            }
        });
    }

    private SharedPreferences prefs() { return getSharedPreferences(PREFS, MODE_PRIVATE); }

    private String server() { return prefs().getString(KEY_SERVER, null); }

    static String origin(Uri u) {
        if (u == null || u.getScheme() == null || u.getHost() == null) return "";
        int port = u.getPort();
        return u.getScheme().toLowerCase() + "://" + u.getHost().toLowerCase() + (port > 0 && port != 443 ? ":" + port : "");
    }

    private boolean trusted(String origin) {
        return LOCAL.equals(origin) || (origin != null && origin.equals(server()));
    }

    private void openExternal(Uri uri) {
        try { startActivity(new Intent(Intent.ACTION_VIEW, uri)); } catch (ActivityNotFoundException ignored) { }
    }

    private class Client extends BridgeWebViewClient {
        Client(Bridge b) { super(b); }

        @Override
        public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) {
            Uri uri = request.getUrl();
            if (trusted(origin(uri))) return false;
            String scheme = uri.getScheme() == null ? "" : uri.getScheme().toLowerCase();
            if (scheme.equals("https") || scheme.equals("http") || scheme.equals("mailto") || scheme.equals("tel")) openExternal(uri);
            return true;
        }

        @Override
        public void onPageStarted(WebView view, String url, Bitmap favicon) {
            currentOrigin = origin(Uri.parse(url));
            super.onPageStarted(view, url, favicon);
        }

        @Override
        public void onReceivedError(WebView view, WebResourceRequest request, WebResourceError error) {
            super.onReceivedError(view, request, error);
            // Sunucuya ulaşılamazsa bağlan ekranına dön (adres değiştirilebilsin).
            if (request.isForMainFrame() && !LOCAL.equals(origin(request.getUrl()))) view.loadUrl(LOCAL + "/index.html?hata=1");
        }
    }

    /** Sayfalara açılan yerel köprü. Yalnızca güvenilen kaynaktaki sayfalar kullanabilir. */
    private class NativeBridge {
        @JavascriptInterface
        public String getServer() {
            String s = server();
            return trusted(currentOrigin) && s != null ? s : "";
        }

        @JavascriptInterface
        public void setServer(String value) {
            if (!LOCAL.equals(currentOrigin)) return; // yalnızca bağlan ekranı adres değiştirir
            Uri u = Uri.parse(value == null ? "" : value.trim());
            if (!"https".equalsIgnoreCase(u.getScheme()) || u.getHost() == null || u.getUserInfo() != null) return;
            String o = origin(u);
            prefs().edit().putString(KEY_SERVER, o).apply();
            runOnUiThread(() -> bridge.getWebView().loadUrl(o + "/panel"));
        }

        @JavascriptInterface
        public void resetServer() {
            if (!trusted(currentOrigin)) return;
            prefs().edit().remove(KEY_SERVER).apply();
            runOnUiThread(() -> bridge.getWebView().loadUrl(LOCAL + "/index.html"));
        }

        /** Web uygulamasının indirdiği dosyayı (base64) önbelleğe yazar ve uygun uygulamayla açar. */
        @JavascriptInterface
        public boolean saveFile(String base64, String fileName, String mime) {
            if (!trusted(currentOrigin) || base64 == null) return false;
            try {
                byte[] data = Base64.decode(base64, Base64.DEFAULT);
                if (data.length > MAX_FILE) return false;
                String cleaned = (fileName == null ? "dosya" : fileName).replaceAll("[\\\\/:*?\"<>|\\p{Cntrl}]", "_");
                final String name = cleaned.isEmpty() || cleaned.startsWith(".") ? "dosya" + cleaned : cleaned;
                File dir = new File(getCacheDir(), "downloads");
                if (!dir.exists() && !dir.mkdirs()) return false;
                File file = new File(dir, name);
                try (FileOutputStream out = new FileOutputStream(file)) { out.write(data); }
                Uri uri = FileProvider.getUriForFile(MainActivity.this, getPackageName() + ".fileprovider", file);
                Intent view = new Intent(Intent.ACTION_VIEW).setDataAndType(uri, mime == null || mime.isEmpty() ? "application/octet-stream" : mime)
                    .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
                runOnUiThread(() -> {
                    try { startActivity(Intent.createChooser(view, name)); } catch (ActivityNotFoundException ignored) { }
                });
                return true;
            } catch (Exception e) {
                return false;
            }
        }
    }
}
