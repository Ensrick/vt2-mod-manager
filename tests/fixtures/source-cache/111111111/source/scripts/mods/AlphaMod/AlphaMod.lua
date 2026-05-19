local mod = get_mod("AlphaMod")

-- Rule 1 collider: same Class+method as BetaMod.
mod:hook_origin(BuffUI, "_align_widgets", function(self, ...)
    return self
end)

-- Rule 1 unique: only AlphaMod touches this; should NOT show up as a conflict.
mod:hook_origin(IngameUI, "destroy", function(self)
    return self
end)

-- Rule 3 collider: same buff name as BetaMod.
BuffTemplates.alpha_shared_buff = {
    buffs = {},
}

-- Rule 3 commented-out: must be ignored.
-- BuffTemplates.commented_buff = {}

-- Rule 3 unique: only AlphaMod.
BuffTemplates["alpha_unique"] = { buffs = {} }
