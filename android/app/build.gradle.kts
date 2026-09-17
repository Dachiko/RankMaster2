plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.kotlin.serialization)
}

android {
    namespace = "com.rankmaster2.phone"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.rankmaster2.phone"
        // Android 10. Below it AV1 does not decode at all, and the library this app ranks has AV1
        // in it (CLIENT_PLAN.md § 1).
        minSdk = 29
        targetSdk = 35
        // Rank Master 3's version, shared with the server and the Windows client
        // (Directory.Build.props carries it for the .NET side). Rank Master 2 is the desktop app
        // being replaced and keeps its own. versionCode only ever climbs - Android refuses to
        // install an APK whose code goes backwards.
        versionCode = 3
        versionName = "3.0.0"
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }

    signingConfigs {
        // One stable key for every build, so an update installs over the last one instead of
        // making the owner uninstall first. The keystore lives outside the repository; without it
        // a debug build still works, it is just signed with the local debug key.
        val keystore = file(System.getenv("RM2_KEYSTORE") ?: "${System.getProperty("user.home")}/.rm2/rm2-release.jks")
        if (keystore.exists()) {
            create("release") {
                storeFile = keystore
                storePassword = System.getenv("RM2_KEYSTORE_PASSWORD") ?: "rankmaster"
                keyAlias = System.getenv("RM2_KEY_ALIAS") ?: "rm2"
                keyPassword = System.getenv("RM2_KEY_PASSWORD") ?: "rankmaster"
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            signingConfigs.findByName("release")?.let { signingConfig = it }
        }
        debug {
            applicationIdSuffix = ".debug"
            versionNameSuffix = "-debug"
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions { jvmTarget = "17" }

    buildFeatures { compose = true }

    packaging {
        resources { excludes += "/META-INF/{AL2.0,LGPL2.1}" }
    }

    testOptions {
        unitTests {
            isIncludeAndroidResources = true
            isReturnDefaultValues = true
        }
    }
}

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.lifecycle.runtime.ktx)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(libs.androidx.activity.compose)
    implementation(platform(libs.androidx.compose.bom))
    implementation(libs.androidx.ui)
    implementation(libs.androidx.ui.graphics)
    implementation(libs.androidx.ui.tooling.preview)
    implementation(libs.androidx.material3)

    implementation(libs.okhttp)
    implementation(libs.kotlinx.serialization.json)
    implementation(libs.androidx.security.crypto)

    implementation(libs.coil.compose)
    implementation(libs.coil.okhttp)

    implementation(libs.media3.exoplayer)
    implementation(libs.media3.ui)
    implementation(libs.media3.datasource.okhttp)

    implementation(libs.camera.camera2)
    implementation(libs.camera.lifecycle)
    implementation(libs.camera.view)
    implementation(libs.mlkit.barcode)

    testImplementation(libs.junit)
    testImplementation(libs.robolectric)
    testImplementation(libs.okhttp.mockwebserver)
    testImplementation(libs.kotlinx.coroutines.test)

    androidTestImplementation(libs.androidx.junit)
    androidTestImplementation(libs.androidx.espresso.core)
    androidTestImplementation(platform(libs.androidx.compose.bom))
    androidTestImplementation(libs.androidx.ui.test.junit4)

    debugImplementation(libs.androidx.ui.tooling)
    debugImplementation(libs.androidx.ui.test.manifest)
}
