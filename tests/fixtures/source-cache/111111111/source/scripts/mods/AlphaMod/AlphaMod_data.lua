return {
    name = "AlphaMod",
    description = "fixture",
    is_togglable = true,
    options = {
        widgets = {
            {
                setting_id = "shared_keybind",
                widget_type = "keybind",
                default_value = {},
                action_name = "alpha_open_menu",
            },
            {
                setting_id = "alpha_only_keybind",
                widget_type = "keybind",
                default_value = {},
                action_name = "alpha_extra",
            },
            {
                -- Not a keybind, so shouldn't count for Rule 2 even if the id matches.
                setting_id = "shared_keybind",
                widget_type = "checkbox",
                default_value = true,
            },
        },
    },
}
