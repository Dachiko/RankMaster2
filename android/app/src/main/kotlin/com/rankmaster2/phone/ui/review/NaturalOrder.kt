package com.rankmaster2.phone.ui.review

import com.rankmaster2.phone.net.MediaRef

/**
 * File names in the order a person expects: digit runs compare as numbers, everything else
 * case-insensitively, so `DSC_9` comes before `DSC_10`.
 *
 * The server sorts by plain ordinal-ignore-case, which puts `DSC_10` first. Review is the one
 * screen that walks a folder in list order, so it re-sorts what it is given. Equal-looking names
 * (`a.jpg` / `A.jpg`, `a1` / `a01`) are ordered by the raw ordinal so the result is stable.
 */
object NaturalOrder : Comparator<String> {

    override fun compare(a: String, b: String): Int {
        var i = 0
        var j = 0
        while (i < a.length && j < b.length) {
            val ca = a[i]
            val cb = b[j]
            if (ca.isDigit() && cb.isDigit()) {
                val endA = runEnd(a, i)
                val endB = runEnd(b, j)
                val numA = a.substring(i, endA).trimStart('0')
                val numB = b.substring(j, endB).trimStart('0')
                if (numA.length != numB.length) return numA.length - numB.length
                val byDigits = numA.compareTo(numB)
                if (byDigits != 0) return byDigits
                i = endA
                j = endB
            } else {
                val byChar = ca.lowercaseChar().compareTo(cb.lowercaseChar())
                if (byChar != 0) return byChar
                i++
                j++
            }
        }
        val byLength = (a.length - i) - (b.length - j)
        if (byLength != 0) return byLength
        return a.compareTo(b)
    }

    private fun runEnd(s: String, from: Int): Int {
        var k = from
        while (k < s.length && s[k].isDigit()) k++
        return k
    }

    fun sorted(items: List<MediaRef>): List<MediaRef> = items.sortedWith { x, y -> compare(x.id, y.id) }
}
