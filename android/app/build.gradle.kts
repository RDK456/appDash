import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

// build-release.ps1 passes -PappVersion=1.2.3 -PupdateFeed=https://host/latest.json and creates keystore.properties.
val appVersion = (findProperty("appVersion") as String?) ?: "1.0.0"
val (major, minor, patch) = appVersion.split(".").map(String::toInt)
val keystore = Properties().apply {
    rootProject.file("keystore.properties").takeIf { it.exists() }?.inputStream()?.use(::load)
}

android {
    namespace = "com.appdash.android"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.appdash.android"
        minSdk = 30 // Android 11: all-files access, MediaStore trash
        targetSdk = 35
        versionCode = major * 10000 + minor * 100 + patch
        versionName = appVersion
        buildConfigField("String", "UPDATE_FEED", "\"${findProperty("updateFeed") ?: ""}\"")
    }
    buildFeatures { buildConfig = true }

    signingConfigs {
        create("release") {
            if (keystore.isNotEmpty()) {
                storeFile = rootProject.file(keystore.getProperty("storeFile"))
                storePassword = keystore.getProperty("storePassword")
                keyAlias = keystore.getProperty("keyAlias")
                keyPassword = keystore.getProperty("keyPassword")
            }
        }
    }
    buildTypes {
        release {
            isMinifyEnabled = false
            signingConfig = signingConfigs.getByName("release")
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
    testOptions { unitTests.isReturnDefaultValues = true }
}

dependencies {
    testImplementation("junit:junit:4.13.2")
}
