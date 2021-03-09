module FrontendWeb.Visuals exposing (..)

import Element
import Svg
import Svg.Attributes


type ActionIcon
    = ChatActionIcon
    | GitHubActionIcon
    | GrowActionIcon
    | ShrinkActionIcon


actionIconSvgElementFromIcon : ActionIcon -> Element.Element event
actionIconSvgElementFromIcon =
    iconSvgElementFromIcon { color = "white", viewBoxWidth = 24, viewBoxHeight = 24 }


iconSvgElementFromIcon : { color : String, viewBoxWidth : Int, viewBoxHeight : Int } -> ActionIcon -> Element.Element event
iconSvgElementFromIcon { color, viewBoxWidth, viewBoxHeight } iconType =
    let
        pathsElements =
            actionIconSvgPathsData iconType
                |> List.map
                    (\pathInfo ->
                        let
                            fillAttributes =
                                if pathInfo.fillNone then
                                    [ Svg.Attributes.fill "none" ]

                                else
                                    []
                        in
                        Svg.path (Svg.Attributes.d pathInfo.pathData :: fillAttributes) []
                    )
    in
    Svg.svg
        [ Svg.Attributes.viewBox ([ 0, 0, viewBoxWidth, viewBoxHeight ] |> List.map String.fromInt |> String.join " ")
        , Svg.Attributes.fill color
        ]
        pathsElements
        |> Element.html


actionIconSvgPathsData : ActionIcon -> List { pathData : String, fillNone : Bool }
actionIconSvgPathsData icon =
    case icon of
        ChatActionIcon ->
            -- https://github.com/google/material-design-icons/tree/96206ade0e8325ac4c4ce9d49dc4ef85241689e1/src/communication/chat_bubble
            [ { pathData = "M20 2H4c-1.1 0-2 .9-2 2v18l4-4h14c1.1 0 2-.9 2-2V4c0-1.1-.9-2-2-2z"
              , fillNone = False
              }
            ]

        GitHubActionIcon ->
            -- https://github.com/microsoft/vscode-codicons/tree/e1155a851abafe070be17d36996474f4f374741f/src/icons
            [ { pathData = "M12 1a11 11 0 1 0 0 22 11 11 0 0 0 0-22zm2.9 19.968h-.086a.471.471 0 0 1-.35-.129.471.471 0 0 1-.129-.34v-1.29c.006-.428.01-.86.01-1.297a3.385 3.385 0 0 0-.139-.943 1.679 1.679 0 0 0-.496-.802 7.34 7.34 0 0 0 1.868-.432 3.715 3.715 0 0 0 1.344-.883c.373-.392.65-.864.81-1.381.196-.632.289-1.29.276-1.952a3.797 3.797 0 0 0-.24-1.353 3.569 3.569 0 0 0-.727-1.177c.068-.172.118-.351.148-.534a3.286 3.286 0 0 0-.036-1.262 4.87 4.87 0 0 0-.203-.7.269.269 0 0 0-.102-.018h-.1c-.21.002-.419.037-.618.102-.22.064-.436.144-.645.239a5.97 5.97 0 0 0-.606.314 9.992 9.992 0 0 0-.525.332 8.78 8.78 0 0 0-4.714 0 12.367 12.367 0 0 0-.525-.332 5.52 5.52 0 0 0-.616-.314 4.14 4.14 0 0 0-.646-.239 2.02 2.02 0 0 0-.607-.102h-.1a.266.266 0 0 0-.1.019 5.356 5.356 0 0 0-.213.699 3.441 3.441 0 0 0-.027 1.262c.03.183.079.362.147.534a3.565 3.565 0 0 0-.726 1.177 3.797 3.797 0 0 0-.24 1.353 6.298 6.298 0 0 0 .266 1.942c.167.517.443.992.811 1.391.38.386.838.687 1.344.883.598.23 1.225.377 1.863.437-.178.161-.32.36-.414.58-.09.219-.153.448-.184.682a2.524 2.524 0 0 1-1.077.248 1.639 1.639 0 0 1-.976-.276 2.661 2.661 0 0 1-.69-.755 2.914 2.914 0 0 0-.267-.35 2.459 2.459 0 0 0-.34-.314 1.687 1.687 0 0 0-.397-.22 1.1 1.1 0 0 0-.441-.093.942.942 0 0 0-.11.01c-.05 0-.1.006-.148.018a.376.376 0 0 0-.12.055.107.107 0 0 0-.054.091.304.304 0 0 0 .129.222c.084.068.155.12.212.157l.026.019c.123.094.24.196.35.305.104.09.197.192.276.303.083.108.154.226.212.349.067.123.138.264.212.424.172.434.478.802.874 1.05.415.223.882.334 1.353.322.16 0 .32-.01.48-.028.156-.025.313-.052.47-.083v1.598a.459.459 0 0 1-.488.477h-.057a9.428 9.428 0 1 1 5.797 0v.005z"
              , fillNone = False
              }
            ]

        GrowActionIcon ->
            [ { pathData = "M14.5 0L14.5 2.5L19.5 2.5L13.5 8.5L15.5 10.5L21.0 5.0L21.0 9.5L24.0 9.5L24.0 0L14.5 0Z"
              , fillNone = False
              }
            , { pathData = "M9.5 24.0L9.5 21.5L4.5 21.5L10.5 15.5L8.5 13.5L3.0 19.0L3.0 14.5L0 14.5L0 24.0L9.5 24.0Z"
              , fillNone = False
              }
            ]

        ShrinkActionIcon ->
            [ { pathData = "M23.0 10.5L23.0 8.0L18.0 8.0L24.0 2.0L22.0 0L16.5 5.5L16.5 1.0L13.5 1.0L13.5 10.5L23.0 10.5Z"
              , fillNone = False
              }
            , { pathData = "M1.0 13.5L1.0 16.0L6.0 16.0L0 22.0L2.0 24.0L7.5 18.5L7.5 23.0L10.5 23.0L10.5 13.5L1.0 13.5Z"
              , fillNone = False
              }
            ]
