package com.appdash.android

import android.app.Activity
import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.content.pm.ApplicationInfo
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import org.json.JSONArray
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.util.concurrent.Executors

// Where each app came from, and whether that source has something newer.
// Play Store and the other stores have no public "is there an update?" API for other apps, so those are opened in
// their store. F-Droid and GitHub publish their releases openly, so those are checked here.
object AppUpdates {
    private val STORES = mapOf(
        "com.android.vending" to "Play Store",
        "org.fdroid.fdroid" to "F-Droid", "org.fdroid.basic" to "F-Droid", "com.looker.droidify" to "F-Droid",
        "com.machiav3lli.fdroid" to "F-Droid", "eu.bubu1.fdroidclassic" to "F-Droid",
        "com.sec.android.app.samsungapps" to "Galaxy Store",
        "com.amazon.venezia" to "Amazon Appstore",
        "com.huawei.appmarket" to "AppGallery",
        "com.aurora.store" to "Aurora Store",
        "dev.imranr.obtainium" to "Obtainium",
    )
    private val SIDELOAD = setOf("com.google.android.packageinstaller", "com.android.packageinstaller", "com.android.shell")

    private fun installer(pm: PackageManager, pkg: String): String? =
        runCatching { pm.getInstallSourceInfo(pkg).installingPackageName }.getOrNull()

    fun source(pm: PackageManager, ai: ApplicationInfo): String {
        val by = installer(pm, ai.packageName)
        return STORES[by] ?: when {
            by == null || by in SIDELOAD -> if (ai.flags and ApplicationInfo.FLAG_SYSTEM != 0) "Preinstalled" else "Sideloaded"
            else -> runCatching { pm.getApplicationLabel(pm.getApplicationInfo(by, 0)).toString() }.getOrDefault(by)
        }
    }

    // ---------- GitHub links (package -> "owner/name"), kept on the phone ----------

    private fun prefs(ctx: Context) = ctx.getSharedPreferences("appdash", Context.MODE_PRIVATE)
    private fun links(ctx: Context) = JSONObject(prefs(ctx).getString("github", "{}") ?: "{}")

    // Accepts "owner/name" or any github.com link to the repo.
    internal fun normalizeRepo(input: String): String? {
        val text = input.trim()
        val parts = text.substringAfter("github.com/", text).trim('/').removeSuffix(".git").split('/')
        if (parts.size < 2) return null
        val ok = Regex("[A-Za-z0-9._-]+")
        return if (ok.matches(parts[0]) && ok.matches(parts[1])) "${parts[0]}/${parts[1]}" else null
    }

    fun link(ctx: Context, pkg: String, repo: String): JSONObject {
        val links = links(ctx)
        if (repo.isBlank()) links.remove(pkg)
        else links.put(pkg, normalizeRepo(repo) ?: throw IllegalArgumentException("Enter the GitHub repo as owner/name, for example ImranR98/Obtainium."))
        prefs(ctx).edit().putString("github", links.toString()).apply()
        val pm = ctx.packageManager
        return item(ctx, pm, pm.getApplicationInfo(pkg, 0), links.optString(pkg))
    }

    // ---------- Checking ----------

    fun check(ctx: Context): JSONObject {
        val pm = ctx.packageManager
        val links = links(ctx)
        val apps = pm.getInstalledApplications(0).filter {
            it.packageName != ctx.packageName && // AppDash updates itself separately
                (it.flags and ApplicationInfo.FLAG_SYSTEM == 0 || it.flags and ApplicationInfo.FLAG_UPDATED_SYSTEM_APP != 0)
        }
        val pool = Executors.newFixedThreadPool(6)
        try {
            val items = apps.map { ai -> pool.submit<JSONObject> { item(ctx, pm, ai, links.optString(ai.packageName)) } }.map { it.get() }
            return JSONObject().put("checkedAt", System.currentTimeMillis()).put("items", JSONArray(items))
        } finally { pool.shutdown() }
    }

    private fun item(ctx: Context, pm: PackageManager, ai: ApplicationInfo, repo: String): JSONObject {
        val info = pm.getPackageInfo(ai.packageName, 0)
        val src = source(pm, ai)
        val o = JSONObject().put("id", ai.packageName).put("name", pm.getApplicationLabel(ai).toString())
            .put("source", src).put("version", info.versionName ?: "").put("repo", repo.ifEmpty { JSONObject.NULL })
        try {
            when {
                repo.isNotEmpty() -> github(repo, info.versionName ?: "", o)
                src == "F-Droid" -> fdroid(ai.packageName, info.longVersionCode, o)
            }
        } catch (e: Exception) { o.put("error", e.message ?: e.toString()) }
        return o
    }

    private fun fdroid(pkg: String, installedCode: Long, o: JSONObject) {
        val j = JSONObject(getText("https://f-droid.org/api/v1/packages/$pkg") ?: run { o.put("note", "Not in the main F-Droid repository."); return })
        val suggested = j.getLong("suggestedVersionCode")
        if (suggested <= installedCode) return
        val pkgs = j.getJSONArray("packages")
        val name = (0 until pkgs.length()).map(pkgs::getJSONObject).firstOrNull { it.getLong("versionCode") == suggested }?.optString("versionName")
        o.put("available", name ?: "build $suggested").put("via", "fdroid")
    }

    private fun github(repo: String, current: String, o: JSONObject) {
        val rel = JSONObject(getText("https://api.github.com/repos/$repo/releases/latest") ?: throw IllegalStateException("$repo has no published release."))
        val tag = rel.optString("tag_name").ifEmpty { rel.optString("name") }
        o.put("latest", tag).put("via", "github")
        if (isNewer(tag, current) != true) return
        o.put("available", tag.removePrefix("v"))
        val assets = rel.getJSONArray("assets").let { a -> (0 until a.length()).map(a::getJSONObject) }
        val i = pickApk(assets.map { it.getString("name") }, Build.SUPPORTED_ABIS.toList())
        if (i < 0) { o.put("note", "The latest release has no APK for this phone."); return }
        val a = assets[i]
        o.put("apkName", a.getString("name")).put("size", a.optLong("size"))
            .put("apkUrl", a.getString("browser_download_url"))
            .put("sha256", a.optString("digest").takeIf { it.startsWith("sha256:") }?.removePrefix("sha256:") ?: JSONObject.NULL)
    }

    private fun getText(url: String): String? {
        val c = URL(url).openConnection() as HttpURLConnection
        c.connectTimeout = 10_000; c.readTimeout = 15_000
        c.setRequestProperty("User-Agent", "AppDash")
        c.setRequestProperty("Accept", "application/vnd.github+json")
        try {
            return when (c.responseCode) {
                200 -> c.inputStream.bufferedReader().readText()
                404 -> null
                403, 429 -> throw IllegalStateException("${c.url.host} is limiting checks right now. Try again in an hour.")
                else -> throw IllegalStateException("${c.url.host} answered ${c.responseCode}.")
            }
        } finally { c.disconnect() }
    }

    // "v1.10.0" vs "1.9.2" -> true. Null when either side has no dotted number to compare.
    internal fun isNewer(latest: String, current: String): Boolean? {
        fun parse(v: String) = Regex("""\d+(\.\d+)*""").find(v)?.value?.split('.')?.map(String::toLong)
        val a = parse(latest) ?: return null
        val b = parse(current) ?: return null
        for (i in 0 until maxOf(a.size, b.size)) {
            val x = a.getOrElse(i) { 0 }; val y = b.getOrElse(i) { 0 }
            if (x != y) return x > y
        }
        return false
    }

    // The APK built for this phone's CPU, else a universal one; never one built for a different CPU.
    internal fun pickApk(names: List<String>, abis: List<String>): Int {
        val apks = names.indices.filter { names[it].endsWith(".apk", ignoreCase = true) }
        val aliases = mapOf("arm64-v8a" to listOf("arm64-v8a", "arm64", "aarch64"), "armeabi-v7a" to listOf("armeabi-v7a", "armv7", "arm32"),
            "x86_64" to listOf("x86_64", "x64"), "x86" to listOf("x86"))
        for (abi in abis) {
            val tags = aliases[abi] ?: listOf(abi)
            apks.firstOrNull { i -> tags.any { names[i].contains(it, ignoreCase = true) } }?.let { return it }
        }
        apks.firstOrNull { names[it].contains("universal", ignoreCase = true) }?.let { return it }
        val anyAbi = aliases.values.flatten()
        return apks.firstOrNull { i -> anyAbi.none { names[i].contains(it, ignoreCase = true) } } ?: -1
    }

    // ---------- Acting ----------

    // Re-reads the linked repo here instead of trusting a URL from the page.
    fun install(ctx: Context, pkg: String, progress: (Int) -> Unit) {
        val repo = links(ctx).optString(pkg).ifEmpty { throw IllegalStateException("Link a GitHub repo to this app first.") }
        val o = JSONObject()
        github(repo, ctx.packageManager.getPackageInfo(pkg, 0).versionName ?: "", o)
        val url = o.optString("apkUrl").ifEmpty { throw IllegalStateException(o.optString("note").ifEmpty { "$repo has nothing newer." }) }
        Installer.install(ctx, URL(url), o.optString("sha256").ifEmpty { null }, pkg, progress)
    }

    // Opens the app's page in the store that installed it; that store shows Update when it has one.
    fun openStore(activity: Activity, pkg: String) {
        val by = installer(activity.packageManager, pkg)
        val market = Intent(Intent.ACTION_VIEW, Uri.parse("market://details?id=$pkg"))
        val attempts = listOfNotNull(by?.let { Intent(market).setPackage(it) }, market,
            Intent(Intent.ACTION_VIEW, Uri.parse("https://play.google.com/store/apps/details?id=$pkg")))
        for (intent in attempts) try { activity.startActivity(intent); return } catch (_: ActivityNotFoundException) { }
    }

    fun openPlayStore(activity: Activity) {
        activity.startActivity(activity.packageManager.getLaunchIntentForPackage("com.android.vending")
            ?: throw IllegalStateException("The Play Store isn't installed on this phone."))
    }
}
