package com.appdash.android

import android.content.pm.ApplicationInfo
import org.junit.Assert.assertEquals
import org.junit.Test

class DataTest {
    private val now = 1_800_000_000_000L
    private val day = 86_400_000L

    @Test fun usageTiersMatchWindows() {
        assertEquals("often", Data.tier(now - day, 0, now))                     // used yesterday
        assertEquals("often", Data.tier(now - 5 * day, 45 * 60_000L, now))     // this week, 45 min on screen
        assertEquals("sometimes", Data.tier(now - 5 * day, 60_000L, now))      // this week, barely
        assertEquals("rare", Data.tier(now - 40 * day, 0, now))
        assertEquals("rare", Data.tier(null, 0, now))                          // no use in the 30-day window
    }

    @Test fun fileTypesByExtension() {
        assertEquals("Videos", Data.categoryOf("clip.MKV"))
        assertEquals("Archives", Data.categoryOf("installer.apk"))
        assertEquals("Documents", Data.categoryOf("report.pdf"))
        assertEquals("Other", Data.categoryOf("noextension"))
        assertEquals("Other", Data.categoryOf("data.qqq"))
    }

    @Test fun appCategories() {
        assertEquals("Games", Data.category(ApplicationInfo.CATEGORY_GAME, false))
        assertEquals("Media", Data.category(ApplicationInfo.CATEGORY_VIDEO, false))
        assertEquals("System", Data.category(ApplicationInfo.CATEGORY_UNDEFINED, true))
        assertEquals("Other", Data.category(ApplicationInfo.CATEGORY_UNDEFINED, false))
    }

    @Test fun versionComparison() {
        assertEquals(true, AppUpdates.isNewer("v1.10.0", "1.9.2"))       // numeric, not alphabetical
        assertEquals(false, AppUpdates.isNewer("1.0", "1.0.0"))          // missing parts count as 0
        assertEquals(false, AppUpdates.isNewer("v2.3.1", "2.3.1 (204)")) // build suffix ignored
        assertEquals(true, AppUpdates.isNewer("2.4.0-beta1", "2.3.9"))
        assertEquals(null, AppUpdates.isNewer("nightly", "1.0"))         // nothing to compare: don't claim an update
    }

    @Test fun apkMatchesThePhoneCpu() {
        val names = listOf("app-x86_64.apk", "app-arm64-v8a.apk", "app-universal.apk", "checksums.txt")
        assertEquals(1, AppUpdates.pickApk(names, listOf("arm64-v8a", "armeabi-v7a")))
        assertEquals(0, AppUpdates.pickApk(names, listOf("x86_64")))
        assertEquals(2, AppUpdates.pickApk(names, listOf("riscv64")))                                   // falls back to universal
        assertEquals(-1, AppUpdates.pickApk(listOf("app-x86_64.apk"), listOf("arm64-v8a")))            // never the wrong CPU
        assertEquals(0, AppUpdates.pickApk(listOf("MyApp-1.2.apk", "notes.txt"), listOf("arm64-v8a"))) // single plain APK
    }

    @Test fun playPageVersion() {
        // Shape of the data block on a Play details page (trimmed).
        assertEquals("157.0", AppUpdates.playVersion("""...,[[["157.0"]],[[[37,"8.0"]]],..."""))
        assertEquals(null, AppUpdates.playVersion("""...,[[["Varies with device"]],[[[24]]],..."""))
        assertEquals(null, AppUpdates.playVersion("<html>no data</html>"))
    }

    @Test fun repoInput() {
        assertEquals("ImranR98/Obtainium", AppUpdates.normalizeRepo("ImranR98/Obtainium"))
        assertEquals("ImranR98/Obtainium", AppUpdates.normalizeRepo("https://github.com/ImranR98/Obtainium/releases"))
        assertEquals("owner/repo", AppUpdates.normalizeRepo("github.com/owner/repo.git"))
        assertEquals(null, AppUpdates.normalizeRepo("just-a-name"))
        assertEquals(null, AppUpdates.normalizeRepo("owner/re po"))
    }

    @Test fun slotOrderMatchesThePalette() {
        // index.html colors segments by this order; changing it breaks the validated adjacent pairs.
        assertEquals(listOf("Apps", "Videos", "Images", "Archives", "Audio", "Documents", "System", "Other"), Data.ORDER)
    }
}
