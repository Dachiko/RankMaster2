package com.rankmaster2.phone.browse

import com.rankmaster2.phone.ui.browse.folderCount
import com.rankmaster2.phone.ui.browse.folderLabel
import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * The heading of the folder list, which used to say "12 folders".
 *
 * That is a count of what is below him and says nothing about where he is standing - and where he
 * is standing is the only question this screen is for. So the heading is the folder's name and the
 * count moves under it.
 *
 * This is the **one** place in the app that takes a Windows path apart, and it is safe only because
 * nothing is done with the answer except draw it. Navigation still uses the server's own `path` and
 * `parent` (section 10.15) - if this function is wrong the owner sees an odd word in a heading, not
 * a request for the wrong folder. These tests are here to keep it honest about the shapes that
 * actually turn up on his PC.
 */
class FolderLabelTest {

    @Test
    fun `an ordinary folder is its last segment`() {
        assertEquals("Iceland", folderLabel("D:\\Photos\\2024\\Iceland"))
        assertEquals("2024", folderLabel("D:\\Photos\\2024"))
    }

    @Test
    fun `a trailing separator does not make the name empty`() {
        assertEquals("Iceland", folderLabel("D:\\Photos\\Iceland\\"))
    }

    @Test
    fun `a drive root shows as the drive, not as nothing`() {
        // "D:\" trims to "D:" and has no separator left in it. An empty heading here would be the
        // one case where the screen looks broken, and it is the case reached by walking up.
        assertEquals("D:", folderLabel("D:\\"))
        assertEquals("C:", folderLabel("C:\\"))
    }

    @Test
    fun `a network share shows the share name`() {
        assertEquals("share", folderLabel("\\\\nas\\share"))
        assertEquals("Trip", folderLabel("\\\\nas\\share\\Trip"))
    }

    @Test
    fun `a forward-slash path works too, because the server may not be Windows`() {
        assertEquals("Iceland", folderLabel("/mnt/photos/Iceland"))
        assertEquals("photos", folderLabel("/mnt/photos/"))
    }

    @Test
    fun `nothing at all is given back as it arrived rather than as an empty heading`() {
        assertEquals("", folderLabel(""))
        assertEquals("\\", folderLabel("\\"))
    }

    @Test
    fun `the count reads as a count and disappears when there is nothing to count`() {
        assertEquals("", folderCount(0))
        assertEquals("1 folder", folderCount(1))
        assertEquals("12 folders", folderCount(12))
    }
}
