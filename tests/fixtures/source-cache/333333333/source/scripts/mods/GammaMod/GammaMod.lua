local mod = get_mod("GammaMod")

-- GammaMod is DISABLED in tests; if the detector ignores disabled mods correctly, this
-- hook_origin should not appear in any conflict, even though it collides with Alpha+Beta.
mod:hook_origin(BuffUI, "_align_widgets", function() end)
BuffTemplates.alpha_shared_buff = {}
