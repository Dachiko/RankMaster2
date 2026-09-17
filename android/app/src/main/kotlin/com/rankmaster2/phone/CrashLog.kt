package com.rankmaster2.phone

import android.content.Context
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Writes the reason the app died to a file, and shows it on the next launch.
 *
 * A phone app that crashes on someone else's device tells you nothing: there is no console, and
 * asking the owner to fetch a log with developer tools is asking them to do the job. This costs one
 * file and turns "it closed itself" into a stack trace that names the line.
 *
 * Kept to the last crash only. Nothing is sent anywhere - it is a file on the phone, shown to the
 * person holding it.
 */
object CrashLog {

    private const val FILE = "last-crash.txt"

    /**
     * A14: `MainActivity.onCreate` calls [install] every time the activity is (re)created - a
     * config change, or coming back from the "pair again" flow - and each call used to wrap
     * whatever handler the previous call had installed. After N recreates one crash was written
     * N times, each wrapper calling the next. One process, one handler.
     */
    private val installed = AtomicBoolean(false)

    fun install(context: Context) {
        if (!installed.compareAndSet(false, true)) return

        val app = context.applicationContext
        val previous = Thread.getDefaultUncaughtExceptionHandler()

        Thread.setDefaultUncaughtExceptionHandler { thread, error ->
            runCatching {
                val when_ = SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.US).format(Date())
                file(app).writeText(
                    buildString {
                        appendLine("Rank Master crashed at $when_")
                        appendLine("thread: ${thread.name}")
                        appendLine()
                        appendLine(error.stackTraceToString())
                    }
                )
            }
            previous?.uncaughtException(thread, error)
        }
    }

    fun lastCrash(context: Context): String? {
        val file = file(context.applicationContext)
        return if (file.exists()) file.readText() else null
    }

    fun clear(context: Context) {
        runCatching { file(context.applicationContext).delete() }
    }

    private fun file(context: Context) = File(context.filesDir, FILE)
}
