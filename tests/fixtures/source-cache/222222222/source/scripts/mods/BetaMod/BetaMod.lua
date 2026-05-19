local mod = get_mod("BetaMod")

-- Rule 1 collider: same Class+method as AlphaMod.
mod:hook_origin(BuffUI, "_align_widgets", function(self)
    return self
end)

-- Rule 3 collider: same buff name as AlphaMod.
BuffTemplates["alpha_shared_buff"] = {
    buffs = {},
}
