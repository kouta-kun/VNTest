using Sandbox;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using SandLang;

namespace Sandbox;

partial class Pawn : AnimatedEntity
{
	private const string MovementDialogue = @"
(label direction
  (text ""Where do you wanna go?"")
					(choice ""-X"" jump decrease-x)
					(choice ""+X"" jump increase-x)
					(choice ""-Y"" jump decrease-y)
					(choice ""+Y"" jump increase-y)
					(choice ""-Z"" cond (cmp iter-count 10) jump decrease-z)
				)

			(label decrease-x
						(text ""Decreasing player X..."")
					(after (body (set* iter-count (+ iter-count 1)) (move self-pawn -10 0 0)) jump direction)
				)

			(label increase-x
						(text ""Increasing player X..."")
					(after (body (set* iter-count (+ iter-count 1)) (move self-pawn 10 0 0)) jump direction)
				)

			(label decrease-y
						(text ""Decreasing player Y..."")
					(after (body (set* iter-count (+ iter-count 1)) (move self-pawn 0 -10 0)) jump direction)
				)

			(label increase-y
						(text ""Increasing player Y..."")
					(after (body (set* iter-count (+ iter-count 1)) (move self-pawn 0 10 0)) jump direction)
				)

			(label decrease-z
						(text ""Decreasing player Z..."")
					(after (body (set* iter-count (+ iter-count 1)) (move self-pawn 0 0 -10)) jump direction)
				)

			(start-dialogue direction)
";

	// at the moment, dialogue attributes have to be networked as separate primitives because I couldn't figure out
	// how to get network serialization of structs working :P
	[Net] public string CurrentDialogueText { get; set; }
	[Net] public List<string> CurrentDialogueChoices { get; set; }

	// Dialogue has to be executed server side (Probably could be networked in some way but I can't imagine that's at
	// all secure for multiplayer-style games, rather just execute it all server-side. It's just dialogue)
	private Dialogue _dialogue = null;
	private Dialogue.Label _currentLabel = null;

	private Hud _status;

	/// <summary>
	/// Called when the entity is first created 
	/// </summary>
	public override void Spawn()
	{
		base.Spawn();

		//
		// Use a watermelon model
		//
		SetModel( "models/sbox_props/watermelon/watermelon.vmdl" );

		EnableDrawing = true;
		EnableHideInFirstPerson = true;
		EnableShadowInFirstPerson = true;
		if ( Game.IsServer )
		{
			_status = new Hud();
			_dialogue = Dialogue.ParseDialogue(
				SParen.ParseText( MovementDialogue ).ToList()
			);

			SetCurrentLabel( _dialogue.InitialLabel );
		}
	}

	private void SetCurrentLabel( Dialogue.Label label )
	{
		_currentLabel = label;
		CurrentDialogueText = label.Text;
		CurrentDialogueChoices = label.Choices != null
			? label.Choices
				.Where( p =>
					(p.Condition == null ||
					 (p.Condition.Execute( GetEnvironment() ) as Value.NumberValue)?.Number > 0) )
				.Select( p => p.ChoiceText )
				.ToList()
			// if no choices are available, we create "Continue..." which will just direct toward afterlabel
			: ContinueChoice;
		Log.Info( $"{CurrentDialogueChoices.Count}: {string.Join( '|', CurrentDialogueChoices )}" );
		CurrentDialogueChoice = 0;
	}

	/// <summary>
	/// Example of a variable read and written using IEnvironment
	/// </summary>
	private int _iterationCount = 0;

	private static readonly List<string> ContinueChoice = new List<string>( new[] { "Continue..." } );

	private IEnvironment GetEnvironment()
	{
		return new EnvironmentMap( new Dictionary<string, Value>()
		{
			["self-pawn"] = new Value.WrapperValue<Pawn>( this ),
			["move"] = new Value.FunctionValue( MovementFunction ),
			["iter-count"] = new Value.NumberValue( _iterationCount ),
		} );
	}

	/// <summary>
	/// example of a C# function mapped into lisp environment
	/// </summary>
	private static Value MovementFunction( IEnvironment environment, Value[] values )
	{
		var target = values[0].Evaluate( environment ) as Value.WrapperValue<Pawn>;
		if ( target is not { Value: { } p } )
		{
			return Value.NoneValue.None;
		}

		var xDelta = (values[1].Evaluate( environment ) as Value.NumberValue)?.Number;
		var yDelta = (values[2].Evaluate( environment ) as Value.NumberValue)?.Number;
		var zDelta = (values[3].Evaluate( environment ) as Value.NumberValue)?.Number;

		if ( xDelta == null || yDelta == null || zDelta == null )
		{
			return Value.NoneValue.None;
		}

		p.Position += new Vector3( (float)(xDelta.Value), (float)yDelta.Value, (float)zDelta.Value );

		return Value.NoneValue.None;
	}

	// An example BuildInput method within a player's Pawn class.
	[ClientInput] public int CurrentDialogueChoice { get; set; }

	public override void BuildInput()
	{
		if ( Input.MouseWheel != 0 )
		{
			CurrentDialogueChoice += Math.Sign( Input.MouseWheel );
			if ( CurrentDialogueChoices is { Count: > 0 } )
			{
				if ( CurrentDialogueChoice < 0 )
				{
					CurrentDialogueChoice = CurrentDialogueChoices.Count + CurrentDialogueChoice;
				}

				CurrentDialogueChoice %= CurrentDialogueChoices.Count;
			}

			Log.Info( $"{CurrentDialogueChoice}" );
		}

		if ( Input.Pressed( InputButton.Use ) )
		{
			DialogueChoice( CurrentDialogueChoice );
		}
	}

	/// <summary>
	/// Called every tick, clientside and serverside.
	/// </summary>
	public override void Simulate( IClient cl )
	{
		base.Simulate( cl );

		Rotation = Angles.Zero.ToRotation();
	}

	// We use a command to trigger dialogue execution
	[ConCmd.Server( "dialogue_choice" )]
	private static void DialogueChoice(int choice)
	{
		var pawn = ConsoleSystem.Caller.Pawn as Pawn;

		pawn!.ExecuteChoice( choice );
	}
	
	private void ExecuteChoice( int choiceIndex )
	{
		if ( Game.IsServer )
		{
			var choice = _currentLabel.Choices?[choiceIndex];
			var dialogueEnvironment = GetEnvironment();
			// when no choices are available, just "Continue" will be an option and it will execute "AfterLabel".
			if ( choice == null && _currentLabel.Choices == null )
			{
				var afterLabel = _currentLabel.AfterLabel;
				foreach ( var codeBlock in afterLabel.CodeBlocks )
				{
					codeBlock.Execute( dialogueEnvironment );
				}

				if ( afterLabel.TargetLabel == null )
				{
					_currentLabel = null;
					CurrentDialogueChoices = null;
					CurrentDialogueText = null;

					_dialogue = null;
					// TODO You would have to go back to your gamemode here, my example dialogue doesn't feature an ending label
				}
				else
				{
					SetCurrentLabel( _dialogue.DialogueLabels[afterLabel.TargetLabel] );
				}
			}
			else if ( choice != null &&
			          (
				          choice.Condition == null ||
				          (choice.Condition.Execute( dialogueEnvironment ) as Value.NumberValue)!
				          .Number > 0
			          )
			        )
			{
				SetCurrentLabel( _dialogue.DialogueLabels[choice.TargetLabel] );
			}

			// read back variable from environment, could be done using a function but this demonstrates variable getting
			var iterCount = (dialogueEnvironment.GetVariable( "iter-count" ) as Value.NumberValue)!.Number;
			_iterationCount = (int)iterCount;
		}
	}

	/// <summary>
	/// Called every frame on the client
	/// </summary>
	public override void FrameSimulate( IClient cl )
	{
		base.FrameSimulate( cl );

		// Update rotation every frame, to keep things smooth
		Rotation = Angles.Zero.ToRotation();

		Camera.Position = Position;
		Camera.Rotation = Rotation;

		// Set field of view to whatever the user chose in options
		Camera.FieldOfView = Screen.CreateVerticalFieldOfView( Game.Preferences.FieldOfView );

		// Set the first person viewer to this, so it won't render our model
		Camera.FirstPersonViewer = this;
	}
}
