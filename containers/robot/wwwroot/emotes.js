(() => {
 'use strict';
 const byId = id => document.getElementById(id), options = byId('robot-emote-options');
 if (!options) return;
 const recording = byId('robot-record'), library = byId('robot-emote-library');
 const ideas = [
  { id: 'wave', name: 'Wave', seconds: 8,
   task: 'Raise one empty gripper slightly, make two small wrist waves, then return to the starting pose.' },
  { id: 'welcome', name: 'Welcome', seconds: 6,
   task: 'Open one empty gripper, make a short outward presenting gesture in clear space, then return to the starting pose.' },
  { id: 'celebrate', name: 'Celebrate', seconds: 6,
   task: 'Lift one empty arm a little, pause, then lower it back to the starting pose.' },
  { id: 'curious', name: 'Curious', seconds: 8,
   task: 'Turn one empty wrist slightly one way, pause, turn it slightly the other way, then return to the starting pose.' },
  { id: 'point', name: 'Point', seconds: 6,
   task: 'Make a short pointing gesture with one empty arm toward clear space, pause, then return to the starting pose.' }
 ];
 let selectedIdea = null, status = null, librarySnapshot = null, playing = false;
 const controls = [];
 function node(tag, text, className) {
  const element = document.createElement(tag);
  if (text) element.textContent = text;
  if (className) element.className = className;
  return element;
 }
 function message(text) {
  const element = byId('robot-emote-message'); element.textContent = text; element.hidden = !text;
 }
 function focusForm(form, field) {
  form.scrollIntoView({ block: 'center' }); form.elements[field].focus({ preventScroll: true });
 }
 function selectSkill(skill) {
  window.dispatchEvent(new CustomEvent('xur-robot-select-skill', { detail: skill }));
  focusForm(byId('robot-skill'), 'assetPath');
 }
 for (const idea of ideas) {
  const card = node('article', '', 'emote-card');
  const heading = node('h3', idea.name);
  const duration = node('span', `${idea.seconds}s recording idea`, 'subtle');
  const description = node('p', idea.task), button = node('button', `Prepare ${idea.name.toLowerCase()} recording`, 'secondary');
  button.type = 'button';
  button.addEventListener('click', () => {
   selectedIdea = idea;
   const arm = byId('robot-emote-arm').value;
   for (const [key, value] of Object.entries({ dataset: `${idea.id}-${arm}`, arm, task: idea.task, seconds: idea.seconds }))
    recording.elements[key].value = value;
   message(`${idea.name} recording details filled in. Choose a new demonstration name for a separate dataset. Arm the robot before recording; afterward, use the recording details to prepare its review.`);
   focusForm(recording, 'dataset');
  });
  card.append(heading, duration, description, button); options.append(card);
 }
 byId('robot-use-recording').addEventListener('click', () => {
  if (!recording.reportValidity()) return;
  const fields = recording.elements;
  selectSkill({ id: fields.dataset.value, name: selectedIdea?.name ?? fields.dataset.value, kind: 'emote',
   arm: fields.arm.value, task: fields.task.value, assetPath: `/state/datasets/${fields.dataset.value}`,
   bin: '', episode: 0, seconds: Number(fields.seconds.value) });
  message('Recording details copied for review. Set the actual episode number and maximum duration, arm for a supervised evaluation, then disarm and save the reviewed emote.');
 });
 function sessionProblem() {
  if (!status?.workloadId) return 'Load the Robotics profile before playing emotes.';
  if (status.emergencyStopLatched) return 'E-stop is latched. Inspect and reset it before arming.';
  if (status.job?.state === 'running') return 'Finish or stop the current robot operation before playing an emote.';
  if (!status.motionEnabled) return 'Enable motion in hardware setup after calibration and clearance checks.';
  if (status.problems.length) return 'Resolve the robot session problems before playing an emote.';
  if (status.stopLatched || !status.armedUntil || Date.parse(status.armedUntil) <= Date.now())
   return 'Arm the robot for a short operator session before playing an emote.';
  return '';
 }
 function reviewProblem(skill) {
  if (!skill.verified) return 'Review required';
  if (!status?.calibrationHash || skill.calibrationHash !== status.calibrationHash) return 'Recalibration requires a new review';
  return '';
 }
 function updateAvailability() {
  const problem = sessionProblem();
  const session = byId('robot-emote-session'), explanation = problem || 'Operator session armed. Each reviewed emote replay ends disarmed.';
  if (session.textContent !== explanation) session.textContent = explanation;
  for (const { skill, play, state, review } of controls) {
   const needsReview = reviewProblem(skill), reason = needsReview || (playing ? 'Emote request in progress' : problem);
   play.disabled = Boolean(reason); play.title = reason;
   state.textContent = needsReview || (playing ? 'Request in progress' : problem ? 'Waiting for operator session' : 'Ready to play');
   review.disabled = playing;
  }
 }
 async function playEmote(skill) {
  if (playing || reviewProblem(skill) || sessionProblem()) { updateAvailability(); return; }
  playing = true; updateAvailability();
  try {
   const job = await window.XurRobot.api('emotes', { skill: skill.id });
   // Keep play disabled until a fresh status arrives, even if it fails to load.
   status = null;
   message(`${skill.name}: ${job.detail ?? 'Emote requested.'}`);
   status = await window.XurRobot.api('status'); renderLibrary();
  } catch (error) { message(error.message); }
  finally { playing = false; updateAvailability(); }
 }
 function renderLibrary() {
  const emotes = (status?.skills ?? []).filter(skill => skill.kind === 'emote');
  const snapshot = JSON.stringify(emotes);
  if (snapshot !== librarySnapshot) {
   librarySnapshot = snapshot; controls.length = 0; library.replaceChildren();
   if (!emotes.length) library.append(node('p', 'No recorded emotes yet. Choose an idea above or use your own recording.', 'subtle'));
   for (const skill of emotes) {
    const card = node('article', '', 'emote-card'), state = node('p', '', 'emote-state');
    const details = node('p', `${skill.arm === 'left' ? 'Left' : 'Right'} arm · episode ${skill.episode} · up to ${skill.seconds}s`, 'subtle');
    const play = node('button', `Play ${skill.name}`, 'primary'), review = node('button', `Review ${skill.name}`, 'secondary');
    play.type = review.type = 'button';
    play.addEventListener('click', () => playEmote(skill));
    review.addEventListener('click', () => selectSkill(skill));
    const actions = node('div', '', 'actions'); actions.append(play, review);
    card.append(node('h3', skill.name), details, node('p', skill.task), state, actions); library.append(card);
    controls.push({ skill, play, state, review });
   }
  }
  updateAvailability();
 }
 window.addEventListener('xur-robot-status', event => { status = event.detail; renderLibrary(); });
 // Arming can expire between the setup page's status polls.
 setInterval(updateAvailability, 1000);
})();
