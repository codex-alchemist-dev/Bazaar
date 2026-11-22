# Bazaar adapter canary. This script is broken on purpose: Godot must report a parse error and exit non-zero.
# If it passes, Godot ignored the flags and the adapter reports itself unavailable.
extends Node

func _init() -> void:
	var broken: int = bazaar_canary_undefined_symbol(
