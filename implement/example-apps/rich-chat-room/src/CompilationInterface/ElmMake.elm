module CompilationInterface.ElmMake exposing (..)

{-| For documentation of the compilation interface, see <https://github.com/pine-vm/pine/blob/main/guide/how-to-use-elm-compilation-interfaces.md#compilationinterfaceelmmake-elm-module>
-}


elm_make____src_Frontend_Main_elm : { debug : { gzip : { base64 : String } }, gzip : { base64 : String } }
elm_make____src_Frontend_Main_elm =
    { gzip = { base64 = "The compiler replaces this value." }
    , debug = { gzip = { base64 = "The compiler replaces this value." } }
    }
