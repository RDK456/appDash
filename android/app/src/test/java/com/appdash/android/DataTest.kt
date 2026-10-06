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

    @Test fun slotOrderMatchesThePalette() {
        // index.html colors segments by this order; changing it breaks the validated adjacent pairs.
        assertEquals(listOf("Apps", "Videos", "Images", "Archives", "Audio", "Documents", "System", "Other"), Data.ORDER)
    }
}
