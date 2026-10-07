package com.appdash.android

import android.app.AppOpsManager
import android.app.usage.StorageStatsManager
import android.app.usage.UsageStatsManager
import android.content.ContentUris
import android.content.Context
import android.content.pm.ApplicationInfo
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.graphics.Canvas
import android.media.MediaScannerConnection
import android.net.Uri
import android.os.Environment
import android.os.Process
import android.os.storage.StorageManager
import android.provider.MediaStore
import android.util.Base64
import org.json.JSONArray
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.io.File
import java.util.PriorityQueue
import java.util.concurrent.CancellationException

// Everything the page shows, read from Android's own services. Mirrors Scanner.cs / DiskScan.cs on Windows.
object Data {
    private const val DAY = 86_400_000L
    private const val TOP_N = 100

    fun hasUsageAccess(ctx: Context): Boolean =
        ctx.getSystemService(AppOpsManager::class.java)
            .unsafeCheckOpNoThrow(AppOpsManager.OPSTR_GET_USAGE_STATS, Process.myUid(), ctx.packageName) == AppOpsManager.MODE_ALLOWED

    // ---------- Apps ----------

    fun apps(ctx: Context): JSONObject {
        val pm = ctx.packageManager
        val usageAccess = hasUsageAccess(ctx)
        val stats = if (usageAccess) usageStats(ctx) else emptyMap()
        val ssm = ctx.getSystemService(StorageStatsManager::class.java)
        val user = Process.myUserHandle()
        val list = JSONArray()
        for (ai in pm.getInstalledApplications(0)) {
            val system = ai.flags and ApplicationInfo.FLAG_SYSTEM != 0
            val launch = pm.getLaunchIntentForPackage(ai.packageName)
            // Like SystemComponent on Windows: system packages with no screen are plumbing, not apps.
            if (system && launch == null) continue
            val info = runCatching { pm.getPackageInfo(ai.packageName, 0) }.getOrNull() ?: continue
            val size = if (!usageAccess) 0L else runCatching {
                ssm.queryStatsForPackage(ai.storageUuid, ai.packageName, user).run { appBytes + dataBytes }
            }.getOrDefault(0L)
            val use = stats[ai.packageName]
            list.put(JSONObject()
                .put("id", ai.packageName)
                .put("name", pm.getApplicationLabel(ai).toString())
                .put("version", info.versionName ?: "")
                .put("kind", if (system) "system" else "user")
                .put("category", category(ai.category, system))
                .put("source", AppUpdates.source(pm, ai))
                .put("size", size)
                .put("lastUsed", use?.first ?: JSONObject.NULL)
                .put("fgMs", use?.second ?: 0L)
                .put("usage", if (usageAccess) tier(use?.first, use?.second ?: 0L) else "unknown")
                .put("installed", info.firstInstallTime)
                .put("updated", info.lastUpdateTime)
                .put("canUninstall", !system || ai.flags and ApplicationInfo.FLAG_UPDATED_SYSTEM_APP != 0)
                .put("canOpen", launch != null)
                .put("icon", icon(pm, ai)))
        }
        return JSONObject().put("usageAccess", usageAccess).put("apps", list)
    }

    internal fun category(cat: Int, system: Boolean) = when (cat) {
        ApplicationInfo.CATEGORY_GAME -> "Games"
        ApplicationInfo.CATEGORY_AUDIO, ApplicationInfo.CATEGORY_VIDEO, ApplicationInfo.CATEGORY_IMAGE -> "Media"
        ApplicationInfo.CATEGORY_SOCIAL -> "Social"
        ApplicationInfo.CATEGORY_PRODUCTIVITY -> "Productivity"
        ApplicationInfo.CATEGORY_NEWS -> "News"
        ApplicationInfo.CATEGORY_MAPS -> "Maps"
        else -> if (system) "System" else "Other"
    }

    private fun usageStats(ctx: Context): Map<String, Pair<Long, Long>> {
        val now = System.currentTimeMillis()
        return ctx.getSystemService(UsageStatsManager::class.java)
            .queryAndAggregateUsageStats(now - 30 * DAY, now)
            .filterValues { it.lastTimeUsed > 0 }
            .mapValues { (_, s) -> s.lastTimeUsed to s.totalTimeInForeground }
    }

    // Same tiers as the Windows app, over Android's 30-day usage window.
    internal fun tier(last: Long?, fgMs: Long, now: Long = System.currentTimeMillis()): String {
        if (last == null) return "rare"
        val days = (now - last) / DAY.toDouble()
        return when {
            days <= 2 || (days <= 7 && fgMs >= 30 * 60_000) -> "often"
            days > 30 -> "rare"
            else -> "sometimes"
        }
    }

    private fun icon(pm: PackageManager, ai: ApplicationInfo): String {
        val px = 72
        val bmp = Bitmap.createBitmap(px, px, Bitmap.Config.ARGB_8888)
        pm.getApplicationIcon(ai).apply { setBounds(0, 0, px, px); draw(Canvas(bmp)) }
        val out = ByteArrayOutputStream()
        bmp.compress(Bitmap.CompressFormat.WEBP_LOSSY, 85, out)
        return "data:image/webp;base64," + Base64.encodeToString(out.toByteArray(), Base64.NO_WRAP)
    }

    fun label(ctx: Context, pkg: String): String =
        runCatching { ctx.packageManager.run { getApplicationLabel(getApplicationInfo(pkg, 0)).toString() } }.getOrDefault(pkg)

    fun storage(ctx: Context): JSONObject {
        val ssm = ctx.getSystemService(StorageStatsManager::class.java)
        return JSONObject()
            .put("total", ssm.getTotalBytes(StorageManager.UUID_DEFAULT))
            .put("free", ssm.getFreeBytes(StorageManager.UUID_DEFAULT))
    }

    // ---------- Storage by file type ----------

    class Item(val path: String, val size: Long, val modified: Long)
    class Cat(val name: String, val fixed: Boolean) { var size = 0L; var count = 0; val files = mutableListOf<Item>() }

    // Fixed slot order = the validated categorical palette order in index.html (adjacent pairs are the checked ones).
    internal val ORDER = listOf("Apps", "Videos", "Images", "Archives", "Audio", "Documents", "System", "Other")
    private val byExt: Map<String, String> = buildMap {
        fun add(cat: String, exts: String) = exts.split(' ').forEach { put(it, cat) }
        add("Videos", "mp4 mkv avi mov wmv flv webm m4v ts 3gp mpg mpeg")
        add("Images", "jpg jpeg png gif bmp webp heic heif tif tiff dng raw svg")
        add("Audio", "mp3 wav flac aac ogg m4a opus amr mid wma")
        add("Archives", "zip rar 7z tar gz tgz bz2 xz iso apk apks xapk")
        add("Documents", "pdf doc docx xls xlsx ppt pptx txt md rtf odt ods odp csv epub")
    }
    internal fun categoryOf(name: String) = byExt[name.substringAfterLast('.', "").lowercase()] ?: "Other"

    @Volatile private var last: List<Cat> = emptyList()
    @Volatile private var lastAt = 0L
    @Volatile private var lastCount = 0L

    fun files(ctx: Context, cancelled: () -> Boolean, progress: (Long, Long, String) -> Unit): JSONObject {
        check(Environment.isExternalStorageManager()) { "Allow All files access to see what's using storage." }
        val cats = ORDER.associateWith { Cat(it, fixed = it == "Apps" || it == "System") }
        val tops = ORDER.associateWith { PriorityQueue<Item>(compareBy { it.size }) }
        var n = 0L
        var filesTotal = 0L
        for (f in Environment.getExternalStorageDirectory().walkTopDown()) {
            if (!f.isFile) continue
            if (++n % 2000 == 0L) {
                if (cancelled()) throw CancellationException()
                progress(n, filesTotal, f.parent ?: "")
            }
            val len = f.length()
            filesTotal += len
            val name = categoryOf(f.name)
            cats.getValue(name).apply { size += len; count++ }
            val q = tops.getValue(name)
            if (q.size < TOP_N) q.add(Item(f.path, len, f.lastModified()))
            else if (len > q.peek()!!.size) { q.poll(); q.add(Item(f.path, len, f.lastModified())) }
        }

        // Apps and system data aren't visible as files; derive them so the parts add up to used space.
        val ssm = ctx.getSystemService(StorageStatsManager::class.java)
        val used = ssm.getTotalBytes(StorageManager.UUID_DEFAULT) - ssm.getFreeBytes(StorageManager.UUID_DEFAULT)
        val apps = cats.getValue("Apps")
        if (hasUsageAccess(ctx)) runCatching { ssm.queryStatsForUser(StorageManager.UUID_DEFAULT, Process.myUserHandle()) }
            .getOrNull()?.let { apps.size = it.appBytes + it.dataBytes }
        apps.count = ctx.packageManager.getInstalledApplications(0).size
        cats.getValue("System").size = maxOf(0L, used - filesTotal - apps.size)

        ORDER.forEach { cats.getValue(it).files += tops.getValue(it).sortedByDescending(Item::size) }
        last = ORDER.map(cats::getValue).filter { it.size > 0 }
        lastAt = System.currentTimeMillis()
        lastCount = n
        return result()
    }

    private fun result(): JSONObject = JSONObject()
        .put("total", last.sumOf { it.size })
        .put("fileCount", lastCount)
        .put("scannedAt", lastAt)
        .put("categories", JSONArray(last.sortedByDescending { it.size }.map { c ->
            JSONObject().put("name", c.name).put("size", c.size).put("count", c.count).put("fixed", c.fixed)
                .put("files", JSONArray(c.files.map { JSONObject().put("path", it.path).put("size", it.size).put("modified", it.modified) }))
        }))

    // Only files the last scan listed are actionable; the page can't name arbitrary paths.
    private fun lookup(path: String): Pair<Cat, Item> {
        for (c in last) c.files.firstOrNull { it.path == path }?.let { return c to it }
        throw IllegalArgumentException("That file isn't in the latest scan. Scan again and retry.")
    }

    fun known(path: String): String = lookup(path).second.path

    fun forget(ctx: Context, path: String): JSONObject {
        val (c, item) = lookup(path)
        synchronized(this) { c.files.remove(item); c.size -= item.size; c.count-- }
        MediaScannerConnection.scanFile(ctx, arrayOf(path), null, null)
        return result()
    }

    fun deleteFile(ctx: Context, path: String): JSONObject {
        val f = File(known(path))
        if (!f.delete() && f.exists()) throw IllegalStateException("Couldn't delete ${f.name}.")
        return forget(ctx, path)
    }

    // MediaStore entry for a path: lets other apps open it and lets the system trash take it.
    @Suppress("DEPRECATION") // DATA is still the only path lookup, and all-files access permits it
    fun mediaUri(ctx: Context, path: String): Uri? {
        val files = MediaStore.Files.getContentUri(MediaStore.VOLUME_EXTERNAL)
        ctx.contentResolver.query(
            files, arrayOf(MediaStore.MediaColumns._ID, MediaStore.Files.FileColumns.MEDIA_TYPE),
            "${MediaStore.MediaColumns.DATA}=?", arrayOf(path), null
        )?.use { c ->
            if (!c.moveToFirst()) return null
            val base = when (c.getInt(1)) {
                MediaStore.Files.FileColumns.MEDIA_TYPE_IMAGE -> MediaStore.Images.Media.EXTERNAL_CONTENT_URI
                MediaStore.Files.FileColumns.MEDIA_TYPE_VIDEO -> MediaStore.Video.Media.EXTERNAL_CONTENT_URI
                MediaStore.Files.FileColumns.MEDIA_TYPE_AUDIO -> MediaStore.Audio.Media.EXTERNAL_CONTENT_URI
                else -> files
            }
            return ContentUris.withAppendedId(base, c.getLong(0))
        }
        return null
    }
}
