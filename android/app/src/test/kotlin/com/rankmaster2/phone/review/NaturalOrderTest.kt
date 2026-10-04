package com.rankmaster2.phone.review

import com.rankmaster2.phone.ui.review.NaturalOrder
import org.junit.Assert.assertEquals
import org.junit.Test

class NaturalOrderTest {

    private fun sort(vararg names: String) = names.sortedWith(NaturalOrder)

    @Test
    fun `digit runs compare as numbers so DSC_9 comes before DSC_10`() {
        assertEquals(
            listOf("DSC_9.jpg", "DSC_10.jpg", "DSC_100.jpg"),
            sort("DSC_100.jpg", "DSC_10.jpg", "DSC_9.jpg"),
        )
    }

    @Test
    fun `everything else is case-insensitive`() {
        assertEquals(listOf("apple.jpg", "Banana.jpg", "cherry.jpg"), sort("cherry.jpg", "Banana.jpg", "apple.jpg"))
    }

    @Test
    fun `several digit runs and leading zeros`() {
        assertEquals(
            listOf("a1b2.jpg", "a1b10.jpg", "a2b1.jpg"),
            sort("a2b1.jpg", "a1b10.jpg", "a1b2.jpg"),
        )
        assertEquals(listOf("img002.jpg", "img10.jpg"), sort("img10.jpg", "img002.jpg"))
    }

    @Test
    fun `names that look equal are still ordered the same way every time`() {
        assertEquals(sort("A.jpg", "a.jpg"), sort("a.jpg", "A.jpg"))
        assertEquals(sort("a01", "a1"), sort("a1", "a01"))
    }

    @Test
    fun `a prefix sorts before the longer name`() {
        assertEquals(listOf("a", "a1", "ab"), sort("ab", "a1", "a"))
    }
}
