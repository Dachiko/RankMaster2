package com.rankmaster2.phone.ui.browse

import android.content.Context
import android.content.SharedPreferences

/**
 * Rule 6: remember the folder that was opened last and offer it first.
 *
 * The owner of this app ranks the same folder for days at a time. Making them walk `D:` → `Photos`
 * → `2024` → `Iceland` on every launch is the difference between a tool and a chore.
 *
 * Nothing secret goes in here - a folder path, not a token - so it lives in plain
 * `SharedPreferences` and not in the encrypted store that [com.rankmaster2.phone.store.Credentials]
 * uses. It is also deliberately *this phone's* memory and not the server's: two phones paired to
 * the same PC each get their own last folder.
 */
interface LastFolderStore {

    /** The folder opened last, or null if this phone has never opened one. */
    fun last(): RememberedFolder?

    fun remember(folder: RememberedFolder)

    /** Called when the remembered folder turns out to be gone, so it stops being offered. */
    fun forget()
}

/** The real one. Needs a `Context`, which is why its test is the one that needs Robolectric. */
class PrefsLastFolderStore(context: Context) : LastFolderStore {

    private val prefs: SharedPreferences =
        context.applicationContext.getSharedPreferences(FILE, Context.MODE_PRIVATE)

    override fun last(): RememberedFolder? {
        val path = prefs.getString(KEY_PATH, null)?.takeIf { it.isNotBlank() } ?: return null
        // The name is only a label. If an older build wrote a path without one, still offer the
        // folder rather than throwing the memory away - the path is the part that matters.
        val name = prefs.getString(KEY_NAME, null)?.takeIf { it.isNotBlank() } ?: path
        return RememberedFolder(path, name)
    }

    override fun remember(folder: RememberedFolder) {
        prefs.edit().putString(KEY_PATH, folder.path).putString(KEY_NAME, folder.name).apply()
    }

    override fun forget() {
        prefs.edit().remove(KEY_PATH).remove(KEY_NAME).apply()
    }

    private companion object {
        const val FILE = "rm2.browse"
        const val KEY_PATH = "lastFolder.path"
        const val KEY_NAME = "lastFolder.name"
    }
}

/** For tests, previews, and any host that has no `Context` to hand. */
class InMemoryLastFolderStore(private var value: RememberedFolder? = null) : LastFolderStore {
    override fun last(): RememberedFolder? = value
    override fun remember(folder: RememberedFolder) { value = folder }
    override fun forget() { value = null }
}
