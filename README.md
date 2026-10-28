# itch.io Licensing for Unity

Add itch.io ownership verification to your Unity game in a few minutes.

The first time a player starts your game, they log in with their itch.io account in the browser. The game asks itch.io whether that account owns the game. If it does, the game unlocks and saves an encrypted license file that only works on that computer. From then on the game starts offline, with no login, until the offline period you choose runs out.

No game can be made uncrackable, and this tool does not try to. Its goal is to make cracking slow and annoying, so your game is not cracked on release day, and to stop one purchased account from being shared freely.

Supported platforms: Windows and Linux desktop builds. WebGL is not supported.

# What it does

* **One time login with itch.io.** The player logs in through the official itch.io page in their own browser. Your game never sees their password.
* **Ownership check.** The game asks the itch.io API whether the logged in account owns your game.
* **Offline license.** After a successful check, the game saves a license file that is encrypted, signed, and bound to the player's computer. Later launches need no internet.
* **Offline period.** You choose how many days the license works offline. After that, the player logs in once more. This limits account sharing.
* **Clock protection.** Turning the system clock back does not extend the offline period.
* **Quiet checks during play.** The license is checked again at random times. A failed check is acted on only after a random delay, so a cracker can't easily see which code caused it.
* **Random values for every project.** Each game gets its own license secret, login port, license file name and file format details, all generated for you. Games made with this tool don't share one pattern that a single cracking tool could target.
* **Safe for public repositories.** Your secrets live in one generated file that is kept out of git automatically.

# Requirements

* Unity 2021.2 or newer.
* Player Settings, Other Settings, API Compatibility Level set to .NET Standard 2.1.
* Scripting Backend set to Mono. The login uses `HttpListener`, which may not work with IL2CPP. If you want to try IL2CPP, test the login carefully on both Windows and Linux first.
* An itch.io account that owns the game page.

# Installation

1. Create a folder anywhere inside your project's `Assets` folder, for example `Assets/ItchLicensing`.
2. Copy these files into it:
    * `ItchOwnershipCheck.cs`
    * `ItchLicenseCache.cs`
    * `ItchSecrets.cs`
    * `ItchStrings.cs`
    * `ItchLicenseConfig.cs`
    * the `Editor` folder with `ItchSecretsGenerator.cs` inside it
3. Keep `ItchSecretsGenerator.cs` inside a folder named `Editor`, so it is never included in your builds.

Do not copy an `ItchSecrets.Local.cs` from anyone else. That file holds one developer's secrets, and the next step creates your own.

When Unity finishes compiling, the Console shows a warning that the secrets are not set. That is expected until you finish the setup.

# Setup

## 1. Open Generate Secrets

In Unity, open **Tools, itch.io Licensing, Generate Secrets**.

The first time, the window fills in random values for your project: the license secret, the login port, the license file name, the salt and the file header. You don't need to change any of them. Leave the window open for the next steps.

## 2. Create the itch.io OAuth application

1. On itch.io, open Settings, then OAuth applications, and create a new application.
2. Use the same account that owns the game page. The ownership check only works for games owned by the creator of the OAuth application.
3. Set the Authorization callback URL to the one shown at the top of the Generate Secrets window. Use the Copy button. It looks like `http://127.0.0.1:27175/callback`, with your own port number.
4. Save, then copy the Client ID.

## 3. Find your Game ID

Open your game's edit page on itch.io. The number in the page address is the Game ID.

## 4. Generate

1. In the Generate Secrets window, enter the Client ID and the Game ID.
2. Set Offline License Days. Use 0 if the license should never expire. Between 30 and 90 days is a good balance between stopping sharing and not bothering honest players.
3. Click **Generate**.

This writes `ItchSecrets.Local.cs` next to `ItchSecrets.cs`, with every value scrambled, and adds it to a `.gitignore` file in the same folder so it never ends up in git.

If you open the window again later, it shows your current values.

## 5. Add the component to your first scene

1. Add `ItchOwnershipCheck` to an object in your first scene, such as your main menu or splash screen.
2. Fill in **Objects To Enable** with what should appear once the game is unlocked, such as the main menu.
3. Fill in **Objects To Disable** with what should show while the game is locked, such as a "Please log in with itch.io" screen.
4. Connect something to **On License Invalid**, such as returning to the main menu or quitting the game.
5. Adjust the timing values if you like:
    * **Login Timeout Minutes**: how long to wait for the player to finish logging in.
    * **Recheck Min and Max Minutes**: the random range between background checks.
    * **Failure Delay Min and Max Seconds**: the random delay before reacting to a failed check.

That's it. Press Play to try it.

# Using it in your code

## Checks in other scenes

The component is not kept between scenes. When a new scene loads, it is destroyed and its background checks stop. The license logic and the result of the online check do survive scene changes.

In later scenes, call `ItchOwnershipCheck.VerifyLicenseAsync()` from a few unrelated places, such as level loads, saving, or unlocking content. It reads the license file only and never goes online.

```csharp
private async void Start()
{
    bool licensed = await ItchOwnershipCheck.VerifyLicenseAsync();

    if (!licensed)
        Invoke(nameof(BreakSomethingLater), Random.Range(60f, 180f));
}
```

If it returns false, react later rather than immediately, for the same reason as the background checks.

## Starting the login yourself

The component starts the login by itself on launch when no valid license is found. To let the player try again, for example after closing the browser tab, add a "Log in with itch.io" button to your locked screen and connect its On Click to `ItchOwnershipCheck.Login`. From code, you can await `StartItchLogin()` instead.

## Signing out

Call `ItchLicenseCache.Delete()` to remove the license file. The next launch asks for login again.

# Changing values after release

Choose the license secret and the Advanced values once before release and keep them.

* **License secret, file name, salt or file header:** if you change any of these in an update, every player has to log in online one more time.
* **Port:** if you change it, you must also change the callback URL on itch.io. Otherwise login stops working, including for players on older versions of your game.
* **Offline License Days:** you can change it at any time. The new value applies to existing licenses too, because the expiry is counted from the last login. For example, if you lower it from 60 to 30 days, a player who logged in 40 days ago has to log in again on their next launch.

# How it works

## First launch

1. The game looks for a license file and finds none.
2. It opens the player's browser on the itch.io login page.
3. The player logs in and approves the permission request.
4. itch.io sends the browser back to a small web server inside the game, at `http://127.0.0.1:<your port>/callback`. The game receives a temporary access token this way.
5. The game asks the itch.io API whether this account owns the game.
6. If it does, the game unlocks and saves an encrypted license file for this computer. The web server is closed.

## Later launches

1. The game finds the license file and checks it.
2. If the file is untouched, matches this game and this computer, and has not expired, the game unlocks right away. No internet is needed.
3. If the file is missing, edited, copied from another computer, or expired, or the hardware changed, the game asks the player to log in again and writes a new file.

## Offline period

Every successful online login starts a new offline period. When it ends, the player has to log in with itch.io once more. Someone who borrowed an account therefore has to log in with it again on every computer, every time the period runs out.

The file also remembers the latest time the game was started. If the system clock is set earlier than that by more than 12 hours, the game treats the license as expired and asks for an online login. The 12 hour margin allows for normal clock corrections.

If the license expires while the player is in the middle of a session, nothing happens. They can finish playing, and the login is requested on the next launch.

## During play

The game quietly checks the license file again every 10 to 25 minutes, at random. If a check fails, nothing happens immediately. After a random delay of 1 to 3 minutes, the game disables its access objects and calls the On License Invalid event.

## The license file

The file contains the game ID, a fingerprint of the computer, the time of the last online login, and the latest time the game was started. It is encrypted with AES using a 256 bit key and signed with HMAC SHA256, so any edit is detected.

The keys are created from your license secret plus the computer's fingerprint, using PBKDF2 with 50,000 rounds. A file copied to another computer therefore cannot be read there.

The computer fingerprint is a SHA256 hash of:

* Unity's device identifier (built from the hardware on Windows, read from the machine id file on Linux)
* the CPU model
* the number of CPU cores
* the operating system family

The GPU, RAM and drivers are not included, so normal upgrades and driver updates do not invalidate the license. Reinstalling the operating system, or replacing the motherboard or CPU, means the player logs in once more.

The file is saved in Unity's persistent data folder, under the random file name generated for your project (for example `802e4f66.dat`):

* Windows: `%USERPROFILE%\AppData\LocalLow\<Company>\<Product>\<file name>`
* Linux: `~/.config/unity3d/<Company>/<Product>/<file name>`

`<Company>` and `<Product>` are the names set in Project Settings, Player.

# Security of the login web server

The login opens a small web server on the player's computer, so it is built to give an attacker nothing to work with:

* It listens only on `127.0.0.1`, so other computers on the network cannot reach it.
* It runs only while the login is in progress and closes as soon as the login succeeds, fails or times out.
* It never reads request bodies. Nothing from a request is executed, saved to disk, used as a file path, or shown back. Every response is fixed text.
* Only a request carrying a secret random value created for that login can finish or cancel it. Anything else is ignored, so a web page or another program cannot inject a token or break the login.
* It accepts only requests from this computer that are addressed to `127.0.0.1` and your port, which blocks DNS rebinding tricks, and it refuses requests from other websites' scripts.
* The login page removes the token from the browser's address bar, so it does not end up in browser history. Strict browser security headers allow only the page's own script to run.
* The token is checked for plain characters and a sensible length before it is used, and it is sent only to `https://api.itch.io`.

The token itself can only read which games the account owns.

# Keeping your secrets out of public code

The scrambling only stops a plain text search. Anyone who has the unscrambling code can reverse it, so the real values must never be in a public repository.

* Your secrets live only in `ItchSecrets.Local.cs`. Without that file the project still compiles, and the game reports that the secrets are not set.
* Before your first push, run `git status` and confirm `ItchSecrets.Local.cs` is not listed.
* If the file is ever pushed, treat the license secret as leaked: create a new one with the Random button and release an update. Players then log in online once more. Removing the file in a later commit is not enough, because it stays in the git history.

Every value in that file is scrambled with its own random key, and the unscramble function itself uses constants picked at random for each project. Games made with this tool therefore don't share the same bytes, port, file name or file header.

Your release builds still contain the secret, because the game needs it to read the license file. Someone who decompiles the game can find it there. That cannot be avoided in a check that runs on the player's computer. An obfuscator makes it much harder.

# Development builds and release builds

Some features exist only in the Unity Editor and in builds with Development Build ticked:

* **Log messages.** In release builds, the compiler removes the log calls and their text, so there is nothing for a cracker to search for.
* **Developer access.** Normally, the creator of a game does not count as owning it on itch.io. In the Editor and development builds, your own developer account is allowed through. Release builds do not contain this code at all.

# Testing

1. In the Editor, press Play, log in, and confirm the game unlocks.
2. Stop and press Play again. It should unlock straight from the license file, even offline.
3. Delete the license file, or call `ItchLicenseCache.Delete()`, to force the login again.
4. To test what real players get, use a normal release build with an account that owns the game. On your game's dashboard, go to Distribute, then Download keys. Generate a key and claim it with a test account.
5. Copy the license file to another computer. The game there should reject it and ask for login.
6. To test expiry, set Offline License Days to 1, generate, log in, then set your system clock two days ahead and start the game. It should ask for login. Set the clock back afterwards.
7. To test clock protection, log in, start the game once, then set your system clock a few days back and start it again. It should ask for login.

# What players will see

* On first launch, a browser tab opens on itch.io asking them to log in and allow the game to check ownership. After approving, they see a short message telling them to return to the game.
* After that, the game starts normally, online or offline, until the offline period ends.
* When the period ends, the itch.io login opens again on the next launch. They need an internet connection for that one login.
* If they reinstall the operating system or replace major hardware, they log in once more.

# Limitations

* This is a check on the player's computer, so a determined cracker can always bypass it, for example by editing the game's code. The protections here only make that slower.
* Use an obfuscator on your builds. It hides the names and logic of these scripts and makes them much harder to read.
* If the player's firewall blocks your game's login port, or another program already uses it, the login cannot complete.
* An expired license cannot be renewed offline. A player without internet cannot play until they reconnect and log in.

# Troubleshooting

* **The Console says the secrets are not set:** run Tools, itch.io Licensing, Generate Secrets.
* **itch.io shows an error about the redirect:** the callback URL in the OAuth application must be exactly the URL shown in the Generate Secrets window, including the port.
* **The game says you do not own it:** in a release build, your developer account does not count. Use an account with a claimed download key.
* **Compile errors about `Rfc2898DeriveBytes`:** set API Compatibility Level to .NET Standard 2.1.
* **The login works in the Editor but not in the build:** check that the Scripting Backend is Mono.
* **The login opens on every launch:** check that the system clock is correct, and that Offline License Days is not set too low.
* **The login says the port is in use:** another program is using your port. Pick a new one with the Random button next to Login Port, update the callback URL on itch.io, and generate again.

# Files

* `ItchOwnershipCheck.cs`: the component you add to your scene. It handles the login, the ownership check, unlocking the game, and the background checks.
* `ItchLicenseCache.cs`: creates, reads and verifies the encrypted license file. It also contains the logging helper that only works in development builds.
* `ItchSecrets.cs`: the public half of the secrets class. It contains no values and is safe to commit.
* `ItchSecrets.Local.cs`: created by Generate Secrets. Your Client ID, Game ID, license secret, offline days and your project's random values, all scrambled. **Never commit or share it.**
* `ItchStrings.cs` and `ItchLicenseConfig.cs`: give the other scripts the values stored in `ItchSecrets.Local.cs`.
* `Editor/ItchSecretsGenerator.cs`: the Generate Secrets window. It must stay inside a folder named `Editor` so it is never included in builds.
* `.gitignore`: keeps `ItchSecrets.Local.cs` out of git.
