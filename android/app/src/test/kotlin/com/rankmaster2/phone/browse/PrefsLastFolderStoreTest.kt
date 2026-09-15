package com.rankmaster2.phone.browse

import com.rankmaster2.phone.ui.browse.PrefsLastFolderStore
import com.rankmaster2.phone.ui.browse.RememberedFolder
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment

/**
 * The one piece of this feature that needs a `Context`, and therefore the one test that needs
 * Robolectric: rule 6's memory has to survive the app being killed, which means it has to actually
 * reach `SharedPreferences` rather than a field.
 */
@RunWith(RobolectricTestRunner::class)
class PrefsLastFolderStoreTest {

    private val context: android.content.Context get() = RuntimeEnvironment.getApplication()

    @Test
    fun `remembers a folder across instances`() {
        PrefsLastFolderStore(context).remember(RememberedFolder("D:\\Photos\\Iceland", "Iceland"))

        // A second instance, as if the process had been restarted.
        val reread = PrefsLastFolderStore(context).last()

        assertEquals(RememberedFolder("D:\\Photos\\Iceland", "Iceland"), reread)
    }

    @Test
    fun `a phone that has never opened a folder remembers nothing`() {
        assertNull(PrefsLastFolderStore(context).last())
    }

    @Test
    fun `forgetting really forgets`() {
        val store = PrefsLastFolderStore(context)
        store.remember(RememberedFolder("E:\\Trip", "Trip"))
        store.forget()

        assertNull(PrefsLastFolderStore(context).last())
    }

    @Test
    fun `the newest folder replaces the last one`() {
        val store = PrefsLastFolderStore(context)
        store.remember(RememberedFolder("D:\\A", "A"))
        store.remember(RememberedFolder("D:\\B", "B"))

        assertEquals(RememberedFolder("D:\\B", "B"), store.last())
    }

    @Test
    fun `a UNC path with backslashes and spaces survives the round trip`() {
        // Paths on this wire are Windows paths seen from Android. Nothing here escapes, splits or
        // normalises them, and this is where that gets checked.
        val ugly = RememberedFolder("\\\\nas\\Media Share\\Holiday 2024 (raw)", "Holiday 2024 (raw)")
        PrefsLastFolderStore(context).remember(ugly)

        assertEquals(ugly, PrefsLastFolderStore(context).last())
    }
}
