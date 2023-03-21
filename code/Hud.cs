using Sandbox.UI;

namespace Sandbox;

public class Hud : HudEntity<RootPanel>
{
	public PlayerStatus PlayerStatus;

	public Hud()
	{
		if ( !Game.IsClient )
		{
			return;
		}

		PlayerStatus = RootPanel.AddChild<PlayerStatus>();
	}
}
