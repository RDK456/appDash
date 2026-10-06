package com.appdash.android

import android.annotation.SuppressLint
import android.app.Activity
import android.app.PendingIntent
import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.content.pm.ApplicationInfo
import android.content.pm.PackageInstaller
import android.graphics.Color
import android.net.Uri
import android.os.Bundle
import android.os.Environment
import android.provider.MediaStore
import android.provider.Settings
import android.view.WindowInsets
import android.view.WindowInsetsController
import android.webkit.JavascriptInterface
import android.webkit.WebChromeClient
import android.webkit.WebView
import android.widget.FrameLayout
import org.json.JSONObject
import java.net.URL
import java.security.MessageDigest
import java.util.concurrent.CancellationException
import java.util.concurrent.Executors

// Hosts assets/index.html in a WebView, like the Windows app hosts its page in WebView2.
// Page -> AppDashNative.call(id, cmd, argJson); we answer window.__native({id, data, error}) or push {event, ...}.
class MainActivity : Activity() {
    private lateinit var root: FrameLayout
    private lateinit var web: WebView
    private val io = Executors.newFixedThreadPool(3)
    private val prefs by lazy { getSharedPreferences("appdash", MODE_PRIVATE) }
    private val results = HashMap<Int, (Int) -> Unit>() // requestCode -> what to do with the system screen's answer
    private var nextRequest = 100
    @Volatile private var cancelScan = false

    @SuppressLint("SetJavaScriptEnabled")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        if (applicationInfo.flags and ApplicationInfo.FLAG_DEBUGGABLE != 0) WebView.setWebContentsDebuggingEnabled(true)
        web = WebView(this).apply {
            settings.javaScriptEnabled = true
            settings.domStorageEnabled = true
            webChromeClient = WebChromeClient() // enables confirm() dialogs
            addJavascriptInterface(Bridge(), "AppDashNative")
        }
        root = FrameLayout(this).apply { addView(web) }
        // Edge-to-edge (Android 15+): keep the page clear of the status and navigation bars.
        root.setOnApplyWindowInsetsListener { v, insets ->
            val bars = insets.getInsets(WindowInsets.Type.systemBars() or WindowInsets.Type.ime())
            v.setPadding(bars.left, bars.top, bars.right, bars.bottom)
            insets
        }
        setContentView(root)
        applyTheme(prefs.getString("theme", "") ?: "")
        web.loadUrl("file:///android_asset/index.html")
    }

    override fun onResume() {
        super.onResume()
        // Coming back from Settings may have granted usage or file access.
        if (::web.isInitialized) web.evaluateJavascript("window.onResume && onResume()", null)
    }

    @Deprecated("Activity back handling; targetSdk 35 still routes it here")
    override fun onBackPressed() {
        web.evaluateJavascript("window.onBack ? onBack() : false") { handled ->
            @Suppress("DEPRECATION") if (handled != "true") super.onBackPressed()
        }
    }

    @Deprecated("No AndroidX here; the platform API is fine")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        results.remove(requestCode)?.invoke(resultCode)
    }

    // PackageInstaller reports back here while updating AppDash itself.
    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        if (intent.action != Updater.ACTION_STATUS) return
        when (val status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE)) {
            PackageInstaller.STATUS_PENDING_USER_ACTION ->
                @Suppress("DEPRECATION") (intent.getParcelableExtra<Intent>(Intent.EXTRA_INTENT))?.let(::startActivity)
            PackageInstaller.STATUS_SUCCESS -> Unit // Android restarts us on the new version
            else -> push(JSONObject().put("event", "appUpdate")
                .put("error", intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE) ?: "Update failed ($status)."))
        }
    }

    inner class Bridge {
        @JavascriptInterface fun theme(): String = prefs.getString("theme", "") ?: ""
        @JavascriptInterface fun systemDark(): Boolean = resources.configuration.isNightModeActive
        @JavascriptInterface fun call(id: Int, cmd: String, arg: String) = dispatch(id, cmd, JSONObject(arg.ifEmpty { "{}" }))
    }

    private fun dispatch(id: Int, cmd: String, a: JSONObject) {
        fun bg(work: () -> Any?) = io.execute { answer(id, work) }
        fun ui(work: () -> Any?) = runOnUiThread { answer(id, work) }
        when (cmd) {
            "apps" -> bg { Data.apps(this) }
            "storage" -> bg { Data.storage(this) }
            "perms" -> bg { JSONObject().put("usage", Data.hasUsageAccess(this)).put("files", Environment.isExternalStorageManager()) }
            "files" -> { cancelScan = false; bg { Data.files(this, { cancelScan }) { n, size, cur ->
                push(JSONObject().put("event", "files").put("files", n).put("size", size).put("current", cur)) } } }
            "cancelFiles" -> { cancelScan = true; answer(id) { true } }
            "grant" -> ui {
                startActivity(if (a.getString("which") == "usage") Intent(Settings.ACTION_USAGE_ACCESS_SETTINGS)
                    else Intent(Settings.ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION, Uri.parse("package:$packageName")))
                true
            }
            "open" -> ui {
                startActivity(packageManager.getLaunchIntentForPackage(a.getString("id"))
                    ?: throw IllegalStateException("This app has no screen to open."))
                true
            }
            "info" -> ui { startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:${a.getString("id")}"))); true }
            "store" -> ui {
                val pkg = a.getString("id")
                try { startActivity(Intent(Intent.ACTION_VIEW, Uri.parse("market://details?id=$pkg"))) }
                catch (_: ActivityNotFoundException) { startActivity(Intent(Intent.ACTION_VIEW, Uri.parse("https://play.google.com/store/apps/details?id=$pkg"))) }
                true
            }
            "uninstall" -> uninstall(id, listOf(a.getString("id")))
            "uninstallMany" -> a.getJSONArray("ids").let { ids -> uninstall(id, List(ids.length(), ids::getString)) }
            "openFile" -> ui {
                val uri = Data.mediaUri(this, Data.known(a.getString("path")))
                    ?: throw IllegalStateException("No app can open this file yet. Try again after the next scan.")
                startActivity(Intent(Intent.ACTION_VIEW).setDataAndType(uri, contentResolver.getType(uri)).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION))
                true
            }
            "trashFile" -> trash(id, a.getString("path"))
            "deleteFile" -> bg { Data.deleteFile(this, a.getString("path")) }
            "theme" -> ui {
                val t = a.getString("theme").takeIf { it == "dark" || it == "light" } ?: throw IllegalArgumentException("Unknown theme.")
                prefs.edit().putString("theme", t).apply(); applyTheme(t); true
            }
            "updateCheck" -> bg { Updater.check(this) }
            "updateInstall" -> bg { Updater.install(this) { pct -> push(JSONObject().put("event", "appUpdate").put("percent", pct)) }; true }
            else -> answer(id) { throw IllegalArgumentException("Unknown command: $cmd") }
        }
    }

    private fun answer(id: Int, work: () -> Any?) {
        val msg = JSONObject().put("id", id)
        try { msg.put("data", work() ?: JSONObject.NULL) }
        catch (_: CancellationException) { msg.put("data", JSONObject.NULL) }
        catch (e: Exception) { msg.put("error", e.message ?: e.toString()) }
        push(msg)
    }

    private fun push(msg: JSONObject) = runOnUiThread { web.evaluateJavascript("window.__native($msg)", null) }

    // One system confirm screen per app, one after another.
    private fun uninstall(id: Int, pkgs: List<String>) = runOnUiThread {
        var i = 0
        fun next() {
            if (i >= pkgs.size) return answer(id) { i }
            push(JSONObject().put("event", "uninstall").put("done", i).put("total", pkgs.size).put("name", Data.label(this, pkgs[i])))
            val req = nextRequest++
            results[req] = { i++; next() }
            @Suppress("DEPRECATION")
            startActivityForResult(Intent(Intent.ACTION_DELETE, Uri.parse("package:${pkgs[i]}")).putExtra(Intent.EXTRA_RETURN_RESULT, true), req)
        }
        next()
    }

    // Android's trash keeps media for 30 days; the system asks the user first.
    private fun trash(id: Int, path: String) = runOnUiThread {
        try {
            val uri = Data.mediaUri(this, Data.known(path))
                ?: throw IllegalStateException("Only photos, videos, audio and downloads can go to the trash. Use Delete instead.")
            val req = nextRequest++
            results[req] = { res -> if (res == RESULT_OK) answer(id) { Data.forget(this, path) } else answer(id) { throw IllegalStateException("Trash cancelled.") } }
            startIntentSenderForResult(MediaStore.createTrashRequest(contentResolver, listOf(uri), true).intentSender, req, null, 0, 0, 0)
        } catch (e: Exception) { answer(id) { throw e } }
    }

    private fun applyTheme(theme: String) {
        val dark = theme == "dark" || (theme != "light" && resources.configuration.isNightModeActive)
        val bg = Color.parseColor(if (dark) "#15181B" else "#F8F6F1")
        root.setBackgroundColor(bg)
        web.setBackgroundColor(bg)
        val light = WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS or WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS
        window.insetsController?.setSystemBarsAppearance(if (dark) 0 else light, light)
    }
}

// Self-update from a feed: latest.json = {versionCode, versionName, apk, sha256, size}, written by build-release.ps1.
object Updater {
    const val ACTION_STATUS = "com.appdash.android.UPDATE_STATUS"

    private fun latest() = JSONObject(URL(BuildConfig.UPDATE_FEED).readText())

    fun check(ctx: Context): JSONObject {
        val cur = ctx.packageManager.getPackageInfo(ctx.packageName, 0)
        if (BuildConfig.UPDATE_FEED.isEmpty()) return JSONObject().put("managed", false).put("version", cur.versionName)
            .put("reason", "This build has no update source. Build it with build-release.ps1.")
        val l = latest()
        val newer = l.getLong("versionCode") > cur.longVersionCode
        return JSONObject().put("managed", true).put("version", cur.versionName)
            .put("available", if (newer) l.getString("versionName") else JSONObject.NULL)
            .put("size", l.optLong("size")).put("delta", false) // Android has no delta installs outside Google Play
    }

    // Streams the APK into a PackageInstaller session. Android shows its own confirm screen and only accepts
    // an APK signed with the same key as this install; the SHA-256 check catches corrupted downloads first.
    fun install(ctx: Context, progress: (Int) -> Unit) {
        val l = latest()
        val apk = URL(URL(BuildConfig.UPDATE_FEED), l.getString("apk"))
        val installer = ctx.packageManager.packageInstaller
        val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL).apply { setAppPackageName(ctx.packageName) }
        val session = installer.openSession(installer.createSession(params))
        try {
            val digest = MessageDigest.getInstance("SHA-256")
            val conn = apk.openConnection()
            val total = conn.contentLengthLong
            conn.getInputStream().use { input ->
                session.openWrite("AppDash.apk", 0, total).use { out ->
                    val buf = ByteArray(64 * 1024)
                    var done = 0L
                    var shown = -1
                    while (true) {
                        val r = input.read(buf)
                        if (r < 0) break
                        out.write(buf, 0, r); digest.update(buf, 0, r); done += r
                        val pct = if (total > 0) (done * 100 / total).toInt() else 0
                        if (pct != shown) { shown = pct; progress(pct) }
                    }
                    session.fsync(out)
                }
            }
            val sha = digest.digest().joinToString("") { "%02x".format(it) }
            if (!sha.equals(l.getString("sha256"), ignoreCase = true)) throw SecurityException("The download didn't match its checksum, so nothing was installed.")
            val status = PendingIntent.getActivity(ctx, 0, Intent(ctx, MainActivity::class.java).setAction(ACTION_STATUS),
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_MUTABLE)
            session.commit(status.intentSender)
        } catch (e: Exception) {
            session.abandon()
            throw e
        } finally {
            session.close()
        }
    }
}
