const num = document.getElementById("number");
let setIntervalID,
  counter = 0;

setIntervalID = setInterval(() => {
  counter++;
  num.textContent = counter + "%";
  if (counter === 65) {
    clearInterval(setIntervalID);
  }
  // 1000/65
}, 15.3);
