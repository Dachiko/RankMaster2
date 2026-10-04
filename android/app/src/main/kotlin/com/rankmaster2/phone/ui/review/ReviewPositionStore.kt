package com.rankmaster2.phone.ui.review

import android.content.Context
import android.content.SharedPreferences

/**
 * Where the owner got to in a folder, so that opening it for review again carries on from there.
 *
 * What is remembered is the **id of the last item he moved past**, not an index: the list changes
 * under him (files are discarded, added, renamed by a rank-rename) and an index would point at a
 * different picture the next time. [ReviewViewModel] decides which item to store; this is only the
 * shelf. Like [com.rankmaster2.phone.ui.browse.LastFolderStore] it is this phone's own memory, in
 * plain `SharedPreferences` - a file name is not a secret.
 */
interface ReviewPositionStore {

    /** The id of the last item passed in [folder], or null if none is remembered. */
    fun lastPassed(folder: String): String?

    fun remember(folder: String, id: String)

    /** Start again: nothing is remembered for [folder]. */
    fun forget(folder: String)
}

class PrefsReviewPositionStore(context: Context) : ReviewPositionStore {

    private val prefs: SharedPreferences =
        context.applicationContext.getSharedPreferences(FILE, Context.MODE_PRIVATE)

    override fun lastPassed(folder: String): String? =
        prefs.getString(key(folder), null)?.takeIf { it.isNotEmpty() }

    override fun remember(folder: String, id: String) {
        prefs.edit().putString(key(folder), id).apply()
    }

    override fun forget(folder: String) {
        prefs.edit().remove(key(folder)).apply()
    }

    private fun key(folder: String) = "pos.$folder"

    private companion object {
        const val FILE = "rm2.review"
    }
}

/** For tests, previews, and any host that has no `Context` to hand. */
class InMemoryReviewPositionStore : ReviewPositionStore {
    private val map = mutableMapOf<String, String>()
    override fun lastPassed(folder: String): String? = map[folder]
    override fun remember(folder: String, id: String) { map[folder] = id }
    override fun forget(folder: String) { map.remove(folder) }
}
