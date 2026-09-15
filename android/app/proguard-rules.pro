# kotlinx.serialization keeps its generated serializers by annotation; without this the release
# build parses nothing and every response looks malformed.
-keepattributes *Annotation*, InnerClasses
-dontnote kotlinx.serialization.**
-keepclassmembers class com.rankmaster2.phone.** {
    *** Companion;
}
-keepclasseswithmembers class com.rankmaster2.phone.** {
    kotlinx.serialization.KSerializer serializer(...);
}
